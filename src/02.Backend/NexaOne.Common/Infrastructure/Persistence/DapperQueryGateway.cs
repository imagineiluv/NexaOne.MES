using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.Abstractions.Models;
using NexaDB.Diagnostics;

namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// Dapper 읽기 명령의 운영 옵션. 기본값은 공급자의 명령 제한 시간을 유지한다.
/// <see cref="Module"/>은 모듈명처럼 정적인 저카디널리티 값만 사용해야 하며 LOT·설비·사용자 식별자를 넣지 않는다.
/// </summary>
public sealed class DapperQueryGatewayOptions
{
    /// <summary>
    /// Dapper <see cref="CommandDefinition"/>에 전달할 제한 시간(초). <see langword="null"/>이면 공급자 기본값을 사용한다.
    /// </summary>
    public int? CommandTimeoutSeconds { get; set; }

    /// <summary>선택적인 정적 모듈명(예: EST, EMS). 진단 태그에만 사용한다.</summary>
    public string? Module { get; set; }
}

/// <summary>
/// <see cref="IQueryGateway"/>의 Dapper 구현(ADR-001). 공급자(<see cref="IDatabaseProvider"/>) 연결을
/// 단일 지점에서 열고 Dapper로 실행한다. 명명 쿼리는 카탈로그로 해석한다. 읽기 경로의 chokepoint.
/// 진단에는 SQL/파라미터/업무 식별자 대신 SHA-256 기반 쿼리 식별자만 기록한다.
/// </summary>
public sealed class DapperQueryGateway : IQueryGateway
{
    private readonly IDatabaseProvider _provider;
    private readonly DatabaseEndpoint _endpoint;
    private readonly IQueryCatalog _catalog;
    private readonly int? _commandTimeoutSeconds;
    private readonly string? _module;
    private readonly IDiagnosticEventSink? _diagnosticSink;

    public DapperQueryGateway(
        EesDataSource dataSource,
        IQueryCatalog? catalog = null,
        DapperQueryGatewayOptions? options = null,
        IDiagnosticEventSink? diagnosticSink = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _provider = dataSource.Provider;
        _endpoint = dataSource.CreateEndpoint();
        _catalog = catalog ?? InMemoryQueryCatalog.Shared;
        var resolvedOptions = options ?? dataSource.QueryGatewayOptions ?? new DapperQueryGatewayOptions();
        _diagnosticSink = diagnosticSink ?? dataSource.QueryDiagnosticSink;

        if (resolvedOptions.CommandTimeoutSeconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                resolvedOptions.CommandTimeoutSeconds,
                "Command timeout must be greater than zero seconds.");
        }

        ValidateModule(resolvedOptions.Module);
        _commandTimeoutSeconds = resolvedOptions.CommandTimeoutSeconds;
        _module = resolvedOptions.Module;
    }

    public Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        QueryCoreAsync<T>(sql, param, queryName: null, ct);

    public Task<T?> QueryFirstOrDefaultAsync<T>(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        ExecuteAsync(
            sql,
            param,
            operation: "query.first-or-default",
            queryName: null,
            static (connection, command) => connection.QueryFirstOrDefaultAsync<T>(command),
            static result => result is null ? 0 : 1,
            ct);

    public Task<TScalar?> ExecuteScalarAsync<TScalar>(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        ExecuteAsync(
            sql,
            param,
            operation: "query.scalar",
            queryName: null,
            static (connection, command) => connection.ExecuteScalarAsync<TScalar>(command),
            static result => result is null ? 0 : 1,
            ct);

    public Task<IReadOnlyList<T>> QueryNamedAsync<T>(string queryName, object? param = null, CancellationToken ct = default)
        => QueryCoreAsync<T>(_catalog.Resolve(queryName), param, queryName, ct);

    private Task<IReadOnlyList<T>> QueryCoreAsync<T>(
        string sql,
        object? param,
        string? queryName,
        CancellationToken ct) =>
        ExecuteAsync(
            sql,
            param,
            operation: "query.list",
            queryName,
            async static (connection, command) =>
            {
                var result = await connection.QueryAsync<T>(command).ConfigureAwait(false);
                return (IReadOnlyList<T>)result.ToList();
            },
            static result => result.Count,
            ct);

    private async Task<TResult> ExecuteAsync<TResult>(
        string sql,
        object? param,
        string operation,
        string? queryName,
        Func<DbConnection, CommandDefinition, Task<TResult>> execute,
        Func<TResult, int> countRows,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var fingerprint = Fingerprint(sql);
        var started = Stopwatch.GetTimestamp();

        try
        {
            using var connection = _provider.CreateConnection(_endpoint.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var command = new CommandDefinition(
                sql,
                param,
                commandTimeout: _commandTimeoutSeconds,
                cancellationToken: ct);
            var result = await execute(connection, command).ConfigureAwait(false);

            await ReportAsync(
                    operation,
                    queryName,
                    fingerprint,
                    QueryOutcome.Succeeded,
                    countRows(result),
                    exception: null,
                    Stopwatch.GetElapsedTime(started))
                .ConfigureAwait(false);

            return result;
        }
        catch (Exception exception)
        {
            var outcome = Classify(exception, ct);
            await ReportAsync(
                    operation,
                    queryName,
                    fingerprint,
                    outcome,
                    rowCount: null,
                    exception,
                    Stopwatch.GetElapsedTime(started))
                .ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask ReportAsync(
        string operation,
        string? queryName,
        string fingerprint,
        QueryOutcome outcome,
        int? rowCount,
        Exception? exception,
        TimeSpan duration)
    {
        if (_diagnosticSink is null)
        {
            return;
        }

        var properties = new Dictionary<string, object?>
        {
            ["query_identifier"] = queryName is null
                ? $"inline:{fingerprint}"
                : $"named:{Fingerprint(queryName)}",
            ["query_fingerprint"] = fingerprint,
            ["query_kind"] = queryName is null ? "inline" : "named",
            ["provider"] = _endpoint.Provider.ToString(),
            ["outcome"] = OutcomeName(outcome),
            ["row_count"] = rowCount,
            ["command_timeout_seconds"] = _commandTimeoutSeconds
        };

        if (_module is not null)
        {
            properties["module"] = _module;
        }

        var diagnosticEvent = new DiagnosticEvent(
            EventId: $"data.dapper-query-gateway.{operation}",
            Timestamp: DateTimeOffset.UtcNow,
            Severity: Severity(outcome),
            Area: "data",
            Component: "dapper-query-gateway",
            Operation: operation,
            Message: Message(outcome),
            EndpointId: _endpoint.EndpointId,
            ExceptionType: exception?.GetType().FullName,
            ExceptionMessage: null,
            Duration: duration,
            Properties: properties);

        // 원래 DB 결과/예외가 진단 sink 실패로 바뀌지 않아야 한다. 취소된 호출도 기록할 수 있도록
        // 호출 토큰 대신 None을 사용하고, NexaDB 확장이 sink 예외를 격리한다.
        await _diagnosticSink.ReportAsync(diagnosticEvent, CancellationToken.None).ConfigureAwait(false);
    }

    private static QueryOutcome Classify(Exception exception, CancellationToken callerToken)
    {
        if (exception is OperationCanceledException)
        {
            return callerToken.IsCancellationRequested
                ? QueryOutcome.Canceled
                : QueryOutcome.TimedOut;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return QueryOutcome.TimedOut;
            }

            // Microsoft.Data.SqlClient/legacy SqlClient의 command timeout 번호. 공급자 패키지를 Common에
            // 직접 의존시키지 않기 위해 읽기 전용 정수 속성만 확인한다.
            if (HasSqlServerTimeoutNumber(current))
            {
                return QueryOutcome.TimedOut;
            }
        }

        return QueryOutcome.Failed;
    }

    private static bool HasSqlServerTimeoutNumber(Exception exception)
    {
        try
        {
            var numberProperty = exception.GetType().GetProperty("Number");
            return numberProperty?.PropertyType == typeof(int)
                && numberProperty.GetValue(exception) is int number
                && number == -2;
        }
        catch
        {
            // 오류 분류는 원래 DB 예외를 절대로 가리면 안 된다.
            return false;
        }
    }

    private static DiagnosticSeverity Severity(QueryOutcome outcome) => outcome switch
    {
        QueryOutcome.Succeeded => DiagnosticSeverity.Information,
        QueryOutcome.Canceled or QueryOutcome.TimedOut => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Error
    };

    private static string Message(QueryOutcome outcome) => outcome switch
    {
        QueryOutcome.Succeeded => "Database query completed.",
        QueryOutcome.Canceled => "Database query was canceled.",
        QueryOutcome.TimedOut => "Database query timed out.",
        _ => "Database query failed."
    };

    private static string OutcomeName(QueryOutcome outcome) => outcome switch
    {
        QueryOutcome.Succeeded => "succeeded",
        QueryOutcome.Canceled => "canceled",
        QueryOutcome.TimedOut => "timed_out",
        _ => "failed"
    };

    private static string Fingerprint(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static void ValidateModule(string? module)
    {
        if (module is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(module)
            || module.Length > 64
            || module.Any(static character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException(
                "Module must be a static identifier of at most 64 letters, digits, '.', '-' or '_' characters.",
                nameof(module));
        }
    }

    private enum QueryOutcome
    {
        Succeeded,
        Canceled,
        TimedOut,
        Failed
    }
}

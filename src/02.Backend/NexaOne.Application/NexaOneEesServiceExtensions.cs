using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;

namespace NexaOne.Application;

public static class NexaOneEesServiceExtensions
{
    public static IServiceCollection AddNexaOneEES(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connStr = configuration.GetConnectionString("NexaOne") ?? string.Empty;
        services.AddSingleton<IRuleDispatcher>(sp =>
            new NexaFrameworkRuleDispatcher(
                sp.GetRequiredService<IDatabaseProvider>(),
                connStr));

        // 파일 기반 쿼리 레지스트리 — 구성된 DB 공급자에 맞는 방언 폴더(db/queries/{mssql|sqlite})를 로드한다.
        // Query:Directory로 폴더를 재지정할 수 있고, 폴더가 없으면 빈 레지스트리로 무해하게 기동한다.
        var dialect = string.Equals(configuration["Database:Provider"], "Sqlite",
            StringComparison.OrdinalIgnoreCase) ? "sqlite" : "mssql";
        var queryDir = configuration["Query:Directory"];
        services.AddSingleton<IQueryRegistry>(_ => FileQueryRegistry.Load(dialect, queryDir));
        return services;
    }
}

internal sealed class NexaFrameworkRuleDispatcher : IRuleDispatcher
{
    private readonly IDatabaseProvider _provider;
    private readonly string _connectionString;

    public NexaFrameworkRuleDispatcher(IDatabaseProvider provider, string connectionString)
    {
        _provider = provider;
        _connectionString = connectionString;
    }

    public async Task<object?> DispatchAsync(
        string ruleName,
        IDictionary<string, object> body,
        CancellationToken ct = default)
    {
        var server = NexusFramework.ApplicationServer.GetInstance();
        try
        {
            var bean = server.GetBean("NexaOne", ruleName);
            var beanType = bean.GetType();

            // Try async: ExecuteAsync(IDictionary<string, object>, CancellationToken)
            var asyncMethod = beanType.GetMethod("ExecuteAsync",
                new[] { typeof(IDictionary<string, object>), typeof(CancellationToken) });
            if (asyncMethod != null)
            {
                var task = asyncMethod.Invoke(bean, new object[] { body, ct });
                if (task is Task<object?> typed) return await typed;
                if (task is Task t) { await t; return null; }
            }

            // Fallback: Execute(IDictionary<string, object>)
            var syncMethod = beanType.GetMethod("Execute",
                new[] { typeof(IDictionary<string, object>) });
            if (syncMethod != null)
                return syncMethod.Invoke(bean, new object[] { body });

            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(
        string sql,
        IDictionary<string, object> parameters,
        CancellationToken ct = default)
    {
        using var conn = _provider.CreateConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        // 파라미터를 명시적으로 추가한다. DBNull은 Dapper의 DynamicParameters 값으로 직접 쓸 수 없으므로
        // CLR null로 변환한다(Dapper가 null → SQL NULL로 바인딩). 선택 필터(@p IS NULL)가 안전히 동작한다.
        DynamicParameters? param = null;
        if (parameters.Count > 0)
        {
            param = new DynamicParameters();
            foreach (var kv in parameters)
                param.Add(kv.Key, kv.Value is DBNull ? null : kv.Value);
        }
        var rows = await conn.QueryAsync(sql, param).ConfigureAwait(false);
        return rows
            .Select(row => ((IDictionary<string, object>)row)
                .ToDictionary(kv => kv.Key, kv => (object?)kv.Value))
            .ToList();
    }

    public async Task<object?> ProcedureAsync(
        string procedureName,
        IDictionary<string, object> parameters,
        CancellationToken ct = default)
    {
        using var conn = _provider.CreateConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var param = new DynamicParameters(parameters);
        return await conn.ExecuteScalarAsync(
            procedureName, param,
            commandType: System.Data.CommandType.StoredProcedure).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, object>> ProcedureToDataSetAsync(
        string procedureName,
        IDictionary<string, object> parameters,
        CancellationToken ct = default)
    {
        using var conn = _provider.CreateConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var param = new DynamicParameters(parameters);
        using var multi = await conn.QueryMultipleAsync(
            procedureName, param,
            commandType: System.Data.CommandType.StoredProcedure).ConfigureAwait(false);

        var result = new Dictionary<string, object>();
        var index = 0;
        while (!multi.IsConsumed)
        {
            var rows = (await multi.ReadAsync().ConfigureAwait(false)).ToList();
            result[$"Table{index++}"] = rows;
        }
        return result;
    }
}

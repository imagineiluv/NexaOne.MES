using System.Data;
using Dapper;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;

namespace NexaOne.Infrastructure.Persistence;

public sealed class ServiceObjectProcessor
{
    private readonly ITransactionManager _txnManager;
    private readonly DatabaseEndpoint _endpoint;
    private readonly string? _currentUserOverride;

    /// <param name="currentUser">감사 사용자 명시 지정(테스트/특수 경로). null이면 요청 앰비언트
    /// (<see cref="CurrentUserContext.UserId"/>)를 호출 시점에 읽고, 그것도 없으면 "SYSTEM"으로 폴백한다.</param>
    public ServiceObjectProcessor(EesDataSource dataSource, string? currentUser = null)
    {
        _txnManager = dataSource.Provider.TransactionManager;
        _endpoint = dataSource.CreateEndpoint();
        _currentUserOverride = currentUser;
    }

    // 감사 사용자: 명시 override > 요청 앰비언트(미들웨어 설정) > "SYSTEM"(백그라운드/비인증).
    private string ResolveAuditUser() => _currentUserOverride ?? CurrentUserContext.UserId ?? "SYSTEM";

    public Task<int> InsertAsync(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        _txnManager.ExecuteInTransactionAsync(_endpoint, async (conn, txn) =>
        {
            var enriched = InjectAudit(param, isInsert: true);
            return await conn.ExecuteAsync(sql, enriched, txn).ConfigureAwait(false);
        }, IsolationLevel.ReadCommitted, ct);

    public Task<int> UpdateAsync(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        _txnManager.ExecuteInTransactionAsync(_endpoint, async (conn, txn) =>
        {
            var enriched = InjectAudit(param, isInsert: false);
            return await conn.ExecuteAsync(sql, enriched, txn).ConfigureAwait(false);
        }, IsolationLevel.ReadCommitted, ct);

    public Task<int> DeleteAsync(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        _txnManager.ExecuteInTransactionAsync(_endpoint, async (conn, txn) =>
            await conn.ExecuteAsync(sql, param, txn).ConfigureAwait(false),
        IsolationLevel.ReadCommitted, ct);

    /// <summary>감사필드 주입 없이 파라미터를 그대로 실행(트랜잭션). capability가 생성한 업서트 SQL +
    /// 컬럼명 파라미터(DynamicParameters/익명객체)에 사용한다 — InjectAudit는 PascalCase 키를 추가하고
    /// 파라미터의 public 프로퍼티를 반영하므로 DynamicParameters와 비호환이라 부적합하다.
    /// 업서트는 감사 컬럼을 호출부에서 직접 채운다(테이블에 감사 컬럼이 있는 경우).</summary>
    public Task<int> ExecuteAsync(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        _txnManager.ExecuteInTransactionAsync(_endpoint, async (conn, txn) =>
            await conn.ExecuteAsync(sql, param, txn).ConfigureAwait(false),
        IsolationLevel.ReadCommitted, ct);

    /// <summary>여러 문장을 단일 트랜잭션에서 순서대로 실행한다(감사 미주입 raw). 어느 한 문장이라도
    /// 실패하면 전체가 롤백되어 부분 커밋이 발생하지 않는다 — 상태 변경과 이력 기록처럼 원자성이
    /// 필요한 복합 쓰기에 사용한다. 영향 행 합계를 반환한다.</summary>
    public Task<int> ExecuteManyAsync(
        CancellationToken ct,
        params (string Sql, object? Param)[] statements) =>
        _txnManager.ExecuteInTransactionAsync(_endpoint, async (conn, txn) =>
        {
            var total = 0;
            foreach (var (sql, param) in statements)
                total += await conn.ExecuteAsync(sql, param, txn).ConfigureAwait(false);
            return total;
        }, IsolationLevel.ReadCommitted, ct);

    /// <summary>
    /// 첫 문장을 낙관적 잠금 guard로 실행하고 정확히 한 행이 바뀐 경우에만 후속 문장을 실행한다.
    /// guard가 0행이면 후속 실행 없이 false를 반환한다. 후속 문장 실패는 동일 트랜잭션 전체를 롤백한다.
    /// </summary>
    public Task<bool> ExecuteGuardedManyAsync(
        CancellationToken ct,
        params (string Sql, object? Param)[] statements)
    {
        if (statements is null || statements.Length == 0)
            throw new ArgumentException("At least one guarded statement is required.", nameof(statements));

        return _txnManager.ExecuteInTransactionAsync(_endpoint, async (conn, txn) =>
        {
            for (var i = 0; i < statements.Length; i++)
            {
                var (sql, param) = statements[i];
                var affected = await conn.ExecuteAsync(sql, param, txn).ConfigureAwait(false);
                if (i == 0 && affected == 0) return false;
                if (affected != 1)
                    throw new DBConcurrencyException(
                        $"Guarded statement {i} affected {affected} rows; expected exactly one.");
            }
            return true;
        }, IsolationLevel.ReadCommitted, ct);
    }

    private Dictionary<string, object?> InjectAudit(object? param, bool isInsert)
    {
        var dict = param is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(
                param.GetType().GetProperties()
                    .ToDictionary(p => p.Name, p => p.GetValue(param)));

        var now = DateTime.UtcNow;
        var auditUser = ResolveAuditUser();
        if (isInsert)
        {
            dict["CreatedBy"] = auditUser;
            dict["CreatedAt"] = now;
        }
        dict["UpdatedBy"] = auditUser;
        dict["UpdatedAt"] = now;
        return dict;
    }
}

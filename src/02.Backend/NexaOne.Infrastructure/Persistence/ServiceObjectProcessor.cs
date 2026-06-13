using System.Data;
using Dapper;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;

namespace NexaOne.Infrastructure.Persistence;

public sealed class ServiceObjectProcessor
{
    private readonly ITransactionManager _txnManager;
    private readonly DatabaseEndpoint _endpoint;
    private readonly string _currentUser;

    public ServiceObjectProcessor(EesDataSource dataSource, string currentUser = "SYSTEM")
    {
        _txnManager = dataSource.Provider.TransactionManager;
        _endpoint = dataSource.CreateEndpoint();
        _currentUser = currentUser;
    }

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

    private Dictionary<string, object?> InjectAudit(object? param, bool isInsert)
    {
        var dict = param is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(
                param.GetType().GetProperties()
                    .ToDictionary(p => p.Name, p => p.GetValue(param)));

        var now = DateTime.UtcNow;
        if (isInsert)
        {
            dict["CreatedBy"] = _currentUser;
            dict["CreatedAt"] = now;
        }
        dict["UpdatedBy"] = _currentUser;
        dict["UpdatedAt"] = now;
        return dict;
    }
}

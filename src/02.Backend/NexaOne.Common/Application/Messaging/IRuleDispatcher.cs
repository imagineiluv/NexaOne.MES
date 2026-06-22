namespace NexaOne.Application.Messaging;

public interface IRuleDispatcher
{
    Task<object?> DispatchAsync(string ruleName, IDictionary<string, object> body, CancellationToken ct = default);
    Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(string sql, IDictionary<string, object> parameters, CancellationToken ct = default);

    /// <summary>파라미터화된 쓰기 SQL(INSERT/UPDATE/DELETE)을 실행하고 영향받은 행 수를 반환한다(명명 쓰기쿼리 게이트웨이).</summary>
    Task<int> ExecuteAsync(string sql, IDictionary<string, object> parameters, CancellationToken ct = default);
    Task<object?> ProcedureAsync(string procedureName, IDictionary<string, object> parameters, CancellationToken ct = default);
    Task<Dictionary<string, object>> ProcedureToDataSetAsync(string procedureName, IDictionary<string, object> parameters, CancellationToken ct = default);
}

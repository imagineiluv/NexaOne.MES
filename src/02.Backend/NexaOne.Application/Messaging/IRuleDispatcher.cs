namespace NexaOne.Application.Messaging;

public interface IRuleDispatcher
{
    Task<object?> DispatchAsync(string ruleName, IDictionary<string, object> body, CancellationToken ct = default);
    Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(string sql, IDictionary<string, object> parameters, CancellationToken ct = default);
    Task<object?> ProcedureAsync(string procedureName, IDictionary<string, object> parameters, CancellationToken ct = default);
    Task<Dictionary<string, object>> ProcedureToDataSetAsync(string procedureName, IDictionary<string, object> parameters, CancellationToken ct = default);
}

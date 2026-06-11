using System.Data;
using Microsoft.Data.SqlClient;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;

namespace NexusCom.Data.MsSql;

// Polling-based Change Feed: queries UPDATED_AT >= @watermark every interval.
// SQL Server CDC (cdc.fn_cdc_get_all_changes) is not required.
public sealed class MsSqlChangeFeedProvider : IChangeFeedProvider
{
    private readonly Dictionary<string, SessionEntry> _sessions = new();

    public Task StartAsync(
        DatabaseEndpoint endpoint,
        IEnumerable<ChangeSubscription> subscriptions,
        Func<ChangeEvent, Task> onEvent,
        CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sub = subscriptions.ToList();

        var intervalMs = endpoint.Options.TryGetValue("polling_interval_ms", out var v)
            && int.TryParse(v, out var i) ? i : 5_000;

        var task = PollLoopAsync(endpoint, sub, onEvent, intervalMs, cts.Token);
        _sessions[endpoint.EndpointId] = new SessionEntry(cts, task);
        return Task.CompletedTask;
    }

    public async Task StopAsync(string endpointId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(endpointId, out var entry)) return;
        entry.Cts.Cancel();
        try { await entry.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); } catch { }
        _sessions.Remove(endpointId);
    }

    private static async Task PollLoopAsync(
        DatabaseEndpoint endpoint,
        IReadOnlyList<ChangeSubscription> subscriptions,
        Func<ChangeEvent, Task> onEvent,
        int intervalMs,
        CancellationToken ct)
    {
        // watermark per subscription: last polled UPDATED_AT
        var watermarks = subscriptions.ToDictionary(s => s.SubscriptionId, _ => DateTime.UtcNow);

        while (!ct.IsCancellationRequested)
        {
            foreach (var sub in subscriptions)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var changes = await FetchChangesAsync(
                        endpoint.ConnectionString, sub, watermarks[sub.SubscriptionId], ct);
                    foreach (var (row, op, ts) in changes)
                    {
                        var evt = BuildEvent(endpoint, sub, row, op, ts);
                        await onEvent(evt);
                        if (ts > watermarks[sub.SubscriptionId])
                            watermarks[sub.SubscriptionId] = ts;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* log or swallow transient errors */ }
            }

            try { await Task.Delay(intervalMs, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static async Task<IReadOnlyList<(IReadOnlyDictionary<string, object?> Row, ChangeOperation Op, DateTime Ts)>>
        FetchChangesAsync(
            string connectionString,
            ChangeSubscription sub,
            DateTime watermark,
            CancellationToken ct)
    {
        var schema = string.IsNullOrEmpty(sub.Schema) ? "dbo" : sub.Schema;
        var sql = $@"
            SELECT *, 'UPDATE' AS __op
            FROM [{schema}].[{sub.Table}] WITH(NOLOCK)
            WHERE UPDATED_AT > @watermark
            ORDER BY UPDATED_AT ASC";

        var results = new List<(IReadOnlyDictionary<string, object?>, ChangeOperation, DateTime)>();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@watermark", watermark);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.Default, ct);

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            DateTime ts = watermark;
            ChangeOperation op = ChangeOperation.Update;

            for (int c = 0; c < reader.FieldCount; c++)
            {
                var colName = reader.GetName(c);
                var value   = reader.IsDBNull(c) ? null : reader.GetValue(c);
                if (colName == "__op")
                    op = ParseOp(value?.ToString());
                else if (colName.Equals("UPDATED_AT", StringComparison.OrdinalIgnoreCase) && value is DateTime dt)
                    ts = dt;
                else
                    row[colName] = value;
            }
            results.Add((row, op, ts));
        }
        return results;
    }

    private static ChangeOperation ParseOp(string? op) => op switch
    {
        "INSERT" => ChangeOperation.Insert,
        "DELETE" => ChangeOperation.Delete,
        _        => ChangeOperation.Update
    };

    private static ChangeEvent BuildEvent(
        DatabaseEndpoint endpoint,
        ChangeSubscription sub,
        IReadOnlyDictionary<string, object?> row,
        ChangeOperation operation,
        DateTime occurredAt)
    {
        var keys   = new Dictionary<string, object?>();
        var deltas = new Dictionary<string, PropertyDelta>();

        foreach (var (k, v) in row)
        {
            if (k.EndsWith("_ID", StringComparison.OrdinalIgnoreCase) && keys.Count == 0)
                keys[k] = v;
            deltas[k] = new PropertyDelta(k, null, v, HasBefore: false, HasAfter: v is not null, IsChanged: true);
        }

        return new ChangeEvent(
            EventId:       Guid.NewGuid().ToString("N"),
            Provider:      DatabaseProviderKind.SqlServer,
            EndpointId:    endpoint.EndpointId,
            Database:      endpoint.DatabaseName ?? string.Empty,
            Schema:        sub.Schema,
            Table:         sub.Table,
            Operation:     operation,
            Keys:          keys,
            Deltas:        deltas,
            OccurredAt:    occurredAt,
            TransactionId: null,
            Position:      occurredAt.Ticks.ToString(),
            Source:        "polling");
    }

    private sealed record SessionEntry(CancellationTokenSource Cts, Task Task);
}

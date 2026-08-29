using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Infrastructure;

/// <summary>
/// Projects durable FDC samples into the material-consumption ledger. The worker deliberately reads
/// the FDC-owned persisted source through a Common bridge into the projection inbox; the realtime
/// message bus is not an accounting source. Deterministic consumption identities make a retry safe
/// after a process crash between the ledger commit and inbox completion.
/// </summary>
public sealed class TraceMaterialConsumptionWorker : BackgroundService
{
    private readonly TraceIngestionService _ingestionService;
    private readonly ITraceProjectionRepository _repository;
    private readonly ConsumptionService _consumptionService;
    private readonly bool _enabled;
    private readonly TimeSpan _pollInterval;
    private readonly int _batchSize;
    internal TimeSpan LeaseReleaseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TraceMaterialConsumptionWorker(
        TraceIngestionService ingestionService,
        ITraceProjectionRepository repository,
        ConsumptionService consumptionService,
        IConfiguration configuration,
        int pollIntervalSeconds = 5,
        int batchSize = 200)
        : this(
            ingestionService,
            repository,
            consumptionService,
            ReadConfiguration(configuration, pollIntervalSeconds, batchSize))
    {
    }

    private TraceMaterialConsumptionWorker(
        TraceIngestionService ingestionService,
        ITraceProjectionRepository repository,
        ConsumptionService consumptionService,
        WorkerConfiguration configuration)
        : this(
            ingestionService,
            repository,
            consumptionService,
            configuration.Enabled,
            configuration.PollIntervalSeconds,
            configuration.BatchSize)
    {
    }

    internal TimeSpan PollInterval => _pollInterval;
    internal int BatchSize => _batchSize;

    public TraceMaterialConsumptionWorker(
        TraceIngestionService ingestionService,
        ITraceProjectionRepository repository,
        ConsumptionService consumptionService,
        bool enabled,
        int pollIntervalSeconds = 5,
        int batchSize = 200)
    {
        _ingestionService = ingestionService ?? throw new ArgumentNullException(nameof(ingestionService));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _consumptionService = consumptionService ?? throw new ArgumentNullException(nameof(consumptionService));
        _enabled = enabled;
        _pollInterval = TimeSpan.FromSeconds(Math.Clamp(pollIntervalSeconds, 1, 3600));
        _batchSize = Math.Clamp(batchSize, 1, 5000);
    }

    private static bool IsTrue(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static WorkerConfiguration ReadConfiguration(
        IConfiguration configuration,
        int pollIntervalSeconds,
        int batchSize)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var legacyKeys = new[]
        {
            "Ivt:TraceProjection:Enabled",
            "Ivt:TraceProjection:PollIntervalSeconds",
            "Ivt:TraceProjection:BatchSize",
        };
        var configuredLegacyKeys = legacyKeys
            .Where(key => configuration[key] is not null)
            .ToArray();
        if (configuredLegacyKeys.Length > 0)
        {
            throw new InvalidOperationException(
                "Legacy TRACE projection settings are not supported. Remove "
                + string.Join(", ", configuredLegacyKeys)
                + " and use Worker:Ivt:TraceMaterialConsumption:* settings explicitly.");
        }

        return new WorkerConfiguration(
            IsTrue(configuration["Worker:Ivt:TraceMaterialConsumption:Enabled"]),
            PositiveInt(
                configuration["Worker:Ivt:TraceMaterialConsumption:PollIntervalSeconds"],
                pollIntervalSeconds),
            PositiveInt(
                configuration["Worker:Ivt:TraceMaterialConsumption:BatchSize"],
                batchSize));
    }

    private static int PositiveInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : Math.Max(1, fallback);

    private sealed record WorkerConfiguration(
        bool Enabled,
        int PollIntervalSeconds,
        int BatchSize);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            Console.WriteLine("[TraceMaterialConsumptionWorker] disabled (enabled=false). Skipping startup.");
            return;
        }

        Console.WriteLine(
            $"[TraceMaterialConsumptionWorker] started (interval={_pollInterval.TotalSeconds:0}s, batchSize={_batchSize}).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProjectBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A transient database failure must not permanently stop projection. Individual row
                // failures are recorded in the inbox by ProjectBatchAsync and retried in source order.
                Console.WriteLine($"[TraceMaterialConsumptionWorker] batch failed: {ex.Message}");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    internal async Task<int> ProjectBatchAsync(CancellationToken ct = default)
    {
        await _ingestionService.EnqueueAsync(_batchSize, ct);
        var items = await _repository.GetPendingAsync(_batchSize, ct);
        var blockedBindings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;

        try
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (blockedBindings.Contains(item.BindingId))
                    continue;

                try
                {
                    if (!string.Equals(item.Quality, "Good", StringComparison.OrdinalIgnoreCase))
                    {
                        await _repository.CompleteAsync(
                            item, null, "Ignored", null, $"Quality:{item.Quality}", ct);
                        completed++;
                        continue;
                    }

                    var feedSessions = await _repository.GetFeedSessionsAsync(
                        item.PlantId, item.EquipmentId, item.FeedPointId, item.CollectedAt, ct);
                    if (feedSessions.Count != 1)
                    {
                        var error = feedSessions.Count == 0
                            ? $"No material feed session covers {item.CollectedAt:O}."
                            : $"Multiple material feed sessions cover {item.CollectedAt:O}.";
                        await _repository.MarkErrorAsync(item, error, ct);
                        blockedBindings.Add(item.BindingId);
                        continue;
                    }

                    var state = await _repository.GetStateAsync(item.BindingId, ct);
                    var decisionResult = _consumptionService.EvaluateTrace(item, state);
                    if (decisionResult.IsFailure)
                    {
                        await _repository.MarkErrorAsync(item, decisionResult.Error.Description, ct);
                        blockedBindings.Add(item.BindingId);
                        continue;
                    }

                    var decision = decisionResult.Value;
                    var nextState = decision.AdvanceState
                        ? new TraceProjectionState(
                            item.BindingId, item.CollectId, item.RawValue, item.CollectedAt)
                        : null;

                    if (decision.Quantity <= 0)
                    {
                        await _repository.CompleteAsync(
                            item, nextState, "Ignored", null, decision.Disposition, ct);
                        completed++;
                        continue;
                    }

                    var feed = feedSessions[0];
                    var identity = CreateIdentity(item.BindingId, item.CollectId);
                    var command = new MaterialConsumptionCommand(
                        ConsumptionId: identity.ConsumptionId,
                        IdempotencyKey: identity.IdempotencyKey,
                        PlantId: item.PlantId,
                        EquipmentId: item.EquipmentId,
                        MaterialLotId: feed.MaterialLotId,
                        MaterialId: feed.MaterialId,
                        Quantity: decision.Quantity,
                        Unit: item.OutputUnit,
                        Mode: "Trace",
                        OccurredAt: item.CollectedAt,
                        SourceSystem: "FDC",
                        SourceEventId: item.CollectId,
                        ProcessLotId: feed.ProcessLotId,
                        WorkOrderId: feed.WorkOrderId,
                        ProcessId: feed.ProcessId,
                        RecipeId: feed.RecipeId,
                        RecipeVersion: feed.RecipeVersion,
                        TraceId: item.CollectId,
                        TagId: item.ParameterId,
                        OperatorId: feed.MountedBy,
                        FeedSessionId: feed.FeedSessionId,
                        CorrelationId: feed.FeedSessionId,
                        MetadataJson: JsonSerializer.Serialize(new
                        {
                            item.BindingId,
                            feed.FeedSessionId,
                            item.CalculationMode,
                            item.RawValue,
                            PreviousValue = state?.LastValue,
                            decision.Disposition,
                        }));

                    var consumption = await _consumptionService.ConsumeAsync(command, ct);
                    if (consumption.IsFailure)
                    {
                        await _repository.MarkErrorAsync(item, consumption.Error.Description, ct);
                        blockedBindings.Add(item.BindingId);
                        continue;
                    }

                    await _repository.CompleteAsync(
                        item, nextState, "Applied", consumption.Value.ConsumptionId,
                        decision.Disposition, ct);
                    completed++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await _repository.MarkErrorAsync(item, ex.Message, ct);
                    blockedBindings.Add(item.BindingId);
                }
            }
        }
        finally
        {
            var leases = items
                .Where(item => !string.IsNullOrWhiteSpace(item.LeaseOwnerId))
                .Select(item => (item.BindingId, LeaseOwnerId: item.LeaseOwnerId!))
                .Distinct()
                .ToList();
            using var cleanup = new CancellationTokenSource(LeaseReleaseTimeout);
            foreach (var lease in leases)
            {
                if (cleanup.IsCancellationRequested) break;
                try
                {
                    // Bound the await as well as the provider call. A database adapter is expected to
                    // honor the cancellation token, but a stuck provider must not hold host shutdown (or
                    // the next projection cycle) past the shared cleanup deadline.
                    await _repository.ReleaseLeaseAsync(
                            lease.BindingId, lease.LeaseOwnerId, cleanup.Token)
                        .WaitAsync(cleanup.Token);
                }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                    // Leases have a durable expiry. Host shutdown must not wait one provider
                    // timeout per binding when best-effort cleanup cannot reach the database.
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[TraceMaterialConsumptionWorker] lease release failed for {lease.BindingId}: {ex.Message}");
                }
            }
        }

        return completed;
    }

    private static (string ConsumptionId, string IdempotencyKey) CreateIdentity(
        string bindingId,
        string collectId)
    {
        var bytes = Encoding.UTF8.GetBytes($"{bindingId}\u001f{collectId}");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return ($"FDC-{hash[..32]}", $"FDC:{hash}");
    }
}

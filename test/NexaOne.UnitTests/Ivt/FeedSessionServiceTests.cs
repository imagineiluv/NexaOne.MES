using FluentAssertions;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.UnitTests.Ivt;

public sealed class FeedSessionServiceTests
{
    [Fact]
    public async Task Mount_uses_authoritative_material_lot_and_replays_identical_command()
    {
        var sessions = new MemoryFeedRepository();
        var lots = new MemoryLotRepository(new MaterialLotState(
            "LOT-01", "MAT-01", "SUP-01", "STORE", 20m, "kg", "InStock", 1));
        var service = new FeedSessionService(sessions, lots);
        var command = MountCommand();

        var mounted = await service.ExecuteAsync(command);
        var replay = await service.ExecuteAsync(command);
        var changed = await service.ExecuteAsync(command with { MaterialId = "MAT-OTHER" });

        mounted.IsSuccess.Should().BeTrue(mounted.IsFailure ? mounted.Error.Description : string.Empty);
        mounted.Value.Should().BeEquivalentTo(new
        {
            MaterialLotId = "LOT-01",
            MaterialId = "MAT-01",
            Status = "Mounted",
            Version = 1,
            IsReplay = false,
        });
        replay.IsSuccess.Should().BeTrue();
        replay.Value.IsReplay.Should().BeTrue();
        changed.Error.Code.Should().Be("IVT.FeedSession.IdempotencyConflict");
        sessions.Writes.Should().ContainSingle();
    }

    [Fact]
    public async Task Mount_rejects_unavailable_or_mismatched_material_lot_before_write()
    {
        var sessions = new MemoryFeedRepository();
        var held = new FeedSessionService(sessions, new MemoryLotRepository(new MaterialLotState(
            "LOT-01", "MAT-01", null, "STORE", 20m, "kg", "Hold", 2)));
        var empty = new FeedSessionService(sessions, new MemoryLotRepository(new MaterialLotState(
            "LOT-02", "MAT-02", null, "STORE", 0m, "kg", "Consumed", 3)));

        var heldResult = await held.ExecuteAsync(MountCommand());
        var emptyResult = await empty.ExecuteAsync(MountCommand() with
        {
            FeedSessionId = "FS-02", MaterialLotId = "LOT-02", MaterialId = "MAT-02",
            IdempotencyKey = "feed-mount-02", SourceEventId = "feed-source-02",
        });
        var mismatch = await new FeedSessionService(sessions, new MemoryLotRepository(new MaterialLotState(
                "LOT-03", "MAT-03", null, "STORE", 1m, "kg", "InStock", 1)))
            .ExecuteAsync(MountCommand() with
            {
                FeedSessionId = "FS-03", MaterialLotId = "LOT-03", MaterialId = "MAT-WRONG",
                IdempotencyKey = "feed-mount-03", SourceEventId = "feed-source-03",
            });

        heldResult.Error.Code.Should().Be("IVT.FeedSession.MaterialUnavailable");
        emptyResult.Error.Code.Should().Be("IVT.FeedSession.MaterialUnavailable");
        mismatch.Error.Code.Should().Be("IVT.FeedSession.MaterialMismatch");
        sessions.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Unmount_is_a_versioned_audited_transition()
    {
        var sessions = new MemoryFeedRepository();
        var lots = new MemoryLotRepository(new MaterialLotState(
            "LOT-01", "MAT-01", null, "STORE", 20m, "kg", "InStock", 1));
        var service = new FeedSessionService(sessions, lots);
        var mounted = await service.ExecuteAsync(MountCommand());

        var unmount = CloseCommand(FeedSessionOperations.Unmount, "feed-unmount-01", "feed-source-u");
        var closed = await service.ExecuteAsync(unmount);
        var replay = await service.ExecuteAsync(unmount);
        var stale = await service.ExecuteAsync(unmount with
        {
            IdempotencyKey = "feed-stale", SourceEventId = "feed-source-stale",
        });

        mounted.IsSuccess.Should().BeTrue();
        closed.IsSuccess.Should().BeTrue(closed.IsFailure ? closed.Error.Description : string.Empty);
        closed.Value.Should().BeEquivalentTo(new
        {
            Status = "Unmounted",
            Version = 2,
            UnmountedBy = "operator-02",
        });
        replay.Value.IsReplay.Should().BeTrue();
        stale.Error.Code.Should().Be("IVT.FeedSession.VersionConflict");
        sessions.Writes.Select(write => write.Operation)
            .Should().Equal(FeedSessionOperations.Mount, FeedSessionOperations.Unmount);
    }

    [Fact]
    public async Task Cancel_is_not_a_published_operation_and_never_reaches_the_repository()
    {
        var sessions = new MemoryFeedRepository();
        var lots = new MemoryLotRepository(new MaterialLotState(
            "LOT-01", "MAT-01", null, "STORE", 20m, "kg", "InStock", 1));
        var service = new FeedSessionService(sessions, lots);

        var cancel = await service.ExecuteAsync(CloseCommand(
            "Cancel", "feed-cancel-unsupported", "feed-source-unsupported"));

        cancel.IsFailure.Should().BeTrue();
        cancel.Error.Description.Should().Contain("Mount or Unmount");
        sessions.Sessions.Should().BeEmpty();
        sessions.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Future_physical_mount_and_unmount_times_are_rejected_before_state_changes()
    {
        var sessions = new MemoryFeedRepository();
        var lots = new MemoryLotRepository(new MaterialLotState(
            "LOT-01", "MAT-01", null, "STORE", 20m, "kg", "InStock", 1));
        var service = new FeedSessionService(sessions, lots);

        var futureMount = await service.ExecuteAsync(MountCommand() with
        {
            OccurredAt = DateTime.UtcNow.AddMinutes(5),
        });

        futureMount.IsFailure.Should().BeTrue();
        futureMount.Error.Description.Should().Contain("future");
        sessions.Writes.Should().BeEmpty();

        (await service.ExecuteAsync(MountCommand())).IsSuccess.Should().BeTrue();
        var futureUnmount = await service.ExecuteAsync(CloseCommand(
            FeedSessionOperations.Unmount, "feed-unmount-future", "feed-source-future") with
        {
            OccurredAt = DateTime.UtcNow.AddMinutes(5),
        });

        futureUnmount.IsFailure.Should().BeTrue();
        futureUnmount.Error.Description.Should().Contain("future");
        sessions.Sessions["FS-01"].Should().BeEquivalentTo(new { Status = "Mounted", Version = 1 });
        sessions.Writes.Should().ContainSingle();
    }

    private static FeedSessionCommand MountCommand() => new(
        FeedSessionOperations.Mount,
        "FS-01",
        0,
        "feed-mount-01",
        "MES",
        "feed-source-01",
        new DateTime(2026, 8, 28, 5, 0, 0, DateTimeKind.Utc),
        PlantId: "PLANT-01",
        EquipmentId: "EQ-01",
        FeedPointId: "FEED-01",
        MaterialLotId: "LOT-01",
        MaterialId: "MAT-01",
        ProcessLotId: "PLOT-01",
        WorkOrderId: "WO-01",
        ProcessId: "PROC-01",
        RecipeId: "RECIPE-01",
        RecipeVersion: 3,
        ActorId: "operator-01",
        CorrelationId: "CORR-01");

    private static FeedSessionCommand CloseCommand(
        string operation,
        string idempotencyKey,
        string sourceEventId,
        string feedSessionId = "FS-01") => new(
        operation,
        feedSessionId,
        1,
        idempotencyKey,
        "MES",
        sourceEventId,
        new DateTime(2026, 8, 28, 6, 0, 0, DateTimeKind.Utc),
        ActorId: "operator-02",
        CorrelationId: "CORR-02",
        Reason: "changeover");

    private sealed class MemoryFeedRepository : IFeedSessionRepository
    {
        public Dictionary<string, FeedSessionState> Sessions { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<FeedSessionWrite> Writes { get; } = [];

        public Task<FeedSessionState?> GetAsync(string feedSessionId, CancellationToken ct = default) =>
            Task.FromResult(Sessions.GetValueOrDefault(feedSessionId));

        public Task<FeedSessionWrite?> GetByIdempotencyKeyAsync(
            string idempotencyKey, CancellationToken ct = default) =>
            Task.FromResult(Writes.SingleOrDefault(write => write.IdempotencyKey == idempotencyKey));

        public Task<FeedSessionWrite?> GetBySourceEventAsync(
            string sourceSystem, string sourceEventId, CancellationToken ct = default) =>
            Task.FromResult(Writes.SingleOrDefault(write =>
                write.SourceSystem == sourceSystem && write.SourceEventId == sourceEventId));

        public Task<bool> TryMountAsync(
            FeedSessionState session, FeedSessionWrite write, CancellationToken ct = default)
        {
            if (Sessions.ContainsKey(session.FeedSessionId) || IsDuplicate(write)
                || Sessions.Values.Any(current => current.Status == "Mounted"
                    && current.PlantId == session.PlantId
                    && current.EquipmentId == session.EquipmentId
                    && current.FeedPointId == session.FeedPointId))
            {
                return Task.FromResult(false);
            }

            Sessions.Add(session.FeedSessionId, session);
            Writes.Add(write);
            return Task.FromResult(true);
        }

        public Task<bool> TryCloseAsync(
            FeedSessionState session,
            int expectedVersion,
            FeedSessionWrite write,
            CancellationToken ct = default)
        {
            if (!Sessions.TryGetValue(session.FeedSessionId, out var current)
                || current.Version != expectedVersion || current.Status != "Mounted"
                || IsDuplicate(write)) return Task.FromResult(false);
            Sessions[session.FeedSessionId] = session;
            Writes.Add(write);
            return Task.FromResult(true);
        }

        private bool IsDuplicate(FeedSessionWrite write) => Writes.Any(current =>
            current.IdempotencyKey == write.IdempotencyKey
            || current.SourceSystem == write.SourceSystem && current.SourceEventId == write.SourceEventId);
    }

    private sealed class MemoryLotRepository(MaterialLotState lot) : IMaterialLotRepository
    {
        public Task<MaterialLotState?> GetLotAsync(string lotId, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(lot.LotId, lotId, StringComparison.OrdinalIgnoreCase)
                ? lot
                : null);

        public Task<MaterialLotTransaction?> GetByIdempotencyKeyAsync(
            string idempotencyKey, CancellationToken ct = default) => Task.FromResult<MaterialLotTransaction?>(null);
        public Task<MaterialLotTransaction?> GetBySourceEventAsync(
            string sourceSystem, string sourceEventId, CancellationToken ct = default) => Task.FromResult<MaterialLotTransaction?>(null);
        public Task<bool> HasFeedSessionReservationAsync(string lotId, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> TryReceiveAsync(MaterialLotTransaction record, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> TryApplyAsync(MaterialLotTransaction record, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}

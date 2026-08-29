using NexaOne.POM.Application.WorkScopes;
using NexaOne.POM.Domain;

namespace NexaOne.UnitTests.Services;

public sealed class WorkScopeServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 29, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Create_is_idempotent_and_rejects_reused_key_with_different_request()
    {
        var repository = new MemoryRepository();
        var service = new WorkScopeService(repository);
        var input = Input("BATCH-01", PomWorkScopeType.Batch) with
        {
            IdempotencyKey = "create-batch-01"
        };

        var first = await service.CreateAsync(input);
        var replay = await service.CreateAsync(input);
        var conflict = await service.CreateAsync(input with { Name = "different" });

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Id.Should().Be(first.Value.Id);
        conflict.IsFailure.Should().BeTrue();
        repository.Scopes.Should().ContainSingle();
    }

    [Fact]
    public async Task Child_scope_is_persisted_as_an_ordered_member_of_campaign()
    {
        var repository = new MemoryRepository();
        var service = new WorkScopeService(repository);
        var campaign = await service.CreateAsync(Input("CAMP-01", PomWorkScopeType.Campaign));
        var child = await service.CreateAsync(Input("BATCH-01", PomWorkScopeType.Batch) with
        {
            ParentScopeId = campaign.Value.Id
        });

        var members = await service.ListMembersAsync(campaign.Value.Id);

        campaign.IsSuccess.Should().BeTrue();
        child.IsSuccess.Should().BeTrue();
        members.IsSuccess.Should().BeTrue();
        members.Value.Should().ContainSingle();
        members.Value[0].MemberScopeId.Should().Be(child.Value.Id);
        members.Value[0].MemberType.Should().Be(PomWorkScopeType.Batch);
        members.Value[0].SequenceNo.Should().Be(1);
    }

    [Fact]
    public async Task Carrier_scope_can_complete_without_a_process_lot_and_keeps_result_attribution()
    {
        var repository = new MemoryRepository();
        var service = new WorkScopeService(repository);
        var created = await service.CreateAsync(Input("CARRIER-01", PomWorkScopeType.Carrier) with
        {
            CarrierId = "CARRIER-01"
        });
        created.IsSuccess.Should().BeTrue();
        created.Value.PlanQty.Should().Be(1m);

        var released = await service.ExecuteAsync(created.Value.Id, Operation(
            PomWorkScopeAction.Release, "release-01", 1));
        var started = await service.ExecuteAsync(created.Value.Id, Operation(
            PomWorkScopeAction.Start, "start-01", 2));
        var completed = await service.ExecuteAsync(created.Value.Id, Operation(
            PomWorkScopeAction.Complete, "complete-01", 3) with
        {
            GoodQty = 1m,
            DefectQty = 0m,
            CarrierId = "CARRIER-01",
            ResultCode = "Pass",
            ResultMetadataJson = "{\"program\":\"CLEAN-01\"}"
        });

        released.IsSuccess.Should().BeTrue();
        started.IsSuccess.Should().BeTrue();
        completed.IsSuccess.Should().BeTrue(completed.IsFailure ? completed.Error.Description : string.Empty);
        completed.Value.Status.Should().Be(PomWorkScopeStatus.Completed);
        var execution = repository.Executions.Single(e => e.IdempotencyKey == "complete-01");
        execution.CarrierId.Should().Be("CARRIER-01");
        execution.ResultCode.Should().Be("Pass");
        execution.ResultMetadataJson.Should().Contain("CLEAN-01");

        var history = await service.ListExecutionsAsync(created.Value.Id);
        history.IsSuccess.Should().BeTrue();
        history.Value.Should().Contain(e => e.IdempotencyKey == "complete-01");
    }

    [Fact]
    public async Task Equipment_is_a_root_scope_and_cannot_be_nested_under_campaign()
    {
        var repository = new MemoryRepository();
        var service = new WorkScopeService(repository);
        var campaign = await service.CreateAsync(Input("CAMP-01", PomWorkScopeType.Campaign));
        var equipment = await service.CreateAsync(Input("EQ-01", PomWorkScopeType.Equipment) with
        {
            ParentScopeId = campaign.Value.Id,
            EquipmentId = "EQ-01"
        });

        var rootEquipment = await service.CreateAsync(Input("EQ-02", PomWorkScopeType.Equipment) with
        {
            EquipmentId = "EQ-02"
        });

        campaign.IsSuccess.Should().BeTrue();
        equipment.IsFailure.Should().BeTrue();
        rootEquipment.IsSuccess.Should().BeTrue();
    }

    private static WorkScopeCreateInput Input(string id, PomWorkScopeType type) => new(
        id, "PLANT-01", type, id, $"{type} {id}", null, null, null, null, null,
        null, type == PomWorkScopeType.Carrier ? 1m : 10m, "operator", null, "operator");

    private static WorkScopeOperationContext Operation(
        PomWorkScopeAction action, string key, int expectedVersion) => new(
        action, "operator", "MOBILE", key, expectedVersion);

    private sealed class MemoryRepository : IWorkScopeRepository
    {
        private readonly Dictionary<string, PomWorkScope> _scopes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PomWorkScopeMember> _members = [];
        private readonly List<PomWorkScopeExecution> _executions = [];

        public IReadOnlyCollection<PomWorkScope> Scopes => _scopes.Values;
        public IReadOnlyList<PomWorkScopeExecution> Executions => _executions;

        public Task<PomWorkScope?> GetByIdAsync(string workScopeId, CancellationToken ct = default)
            => Task.FromResult(_scopes.TryGetValue(workScopeId, out var scope) ? scope : null);

        public Task<PomWorkScope?> GetByIdempotencyKeyAsync(
            string idempotencyKey, CancellationToken ct = default)
            => Task.FromResult(_scopes.Values.FirstOrDefault(scope =>
                string.Equals(scope.CreateIdempotencyKey, idempotencyKey, StringComparison.Ordinal)));

        public Task<IReadOnlyList<PomWorkScope>> ListAsync(
            string? plantId, PomWorkScopeType? scopeType, string? targetId,
            string? parentScopeId, PomWorkScopeStatus? status,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PomWorkScope>>(_scopes.Values
                .Where(scope => plantId is null || scope.PlantId == plantId)
                .Where(scope => scopeType is null || scope.ScopeType == scopeType)
                .Where(scope => targetId is null || scope.TargetId == targetId)
                .Where(scope => parentScopeId is null || scope.ParentScopeId == parentScopeId)
                .Where(scope => status is null || scope.Status == status)
                .ToList());

        public Task<IReadOnlyList<PomWorkScopeMember>> ListMembersAsync(
            string workScopeId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PomWorkScopeMember>>(_members
                .Where(member => member.WorkScopeId == workScopeId)
                .OrderBy(member => member.SequenceNo)
                .ToList());

        public Task<IReadOnlyList<PomWorkScopeExecution>> ListExecutionsAsync(
            string workScopeId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PomWorkScopeExecution>>(_executions
                .Where(execution => execution.WorkScopeId == workScopeId)
                .OrderByDescending(execution => execution.OccurredAt)
                .ToList());

        public Task<PomWorkScopeExecution?> GetExecutionByIdempotencyKeyAsync(
            string idempotencyKey, CancellationToken ct = default)
            => Task.FromResult(_executions.FirstOrDefault(execution =>
                execution.IdempotencyKey == idempotencyKey));

        public Task AddAsync(PomWorkScope scope, CancellationToken ct = default)
        {
            _scopes.Add(scope.Id, scope);
            if (scope.ParentScopeId is not null)
            {
                _members.Add(new PomWorkScopeMember(
                    $"MEM-{scope.Id}", scope.ParentScopeId, scope.Id, scope.ScopeType,
                    scope.TargetId, _members.Count(member => member.WorkScopeId == scope.ParentScopeId) + 1,
                    Now));
            }
            return Task.CompletedTask;
        }

        public Task<bool> UpdateWithExecutionAsync(
            PomWorkScope scope, PomWorkScopeExecution execution,
            CancellationToken ct = default)
        {
            if (!_scopes.TryGetValue(scope.Id, out var persisted)
                || persisted.VersionNo != execution.ExpectedVersion)
                return Task.FromResult(false);
            scope.AcceptPersistedVersion();
            _scopes[scope.Id] = scope;
            _executions.Add(execution);
            return Task.FromResult(true);
        }
    }
}

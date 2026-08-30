using System.Text.Json;
using NexaOne.Project.Cleaner;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.UnitTests.Projects;

public sealed class CleanerWorkScopeProjectionPolicyTests
{
    private readonly CleanerWorkScopeProjectionPolicy _sut = new();

    [Fact]
    public void Identity_IsStableAndVersioned()
    {
        _sut.Identity.PolicyId.Should().Be(CleanerWorkScopeProjectionPolicy.PolicyId);
        _sut.Identity.Version.Should().Be(CleanerWorkScopeProjectionPolicy.PolicyVersion);
    }

    [Fact]
    public void Running_CreatedHeldPair_ConvergesInLegalOrder()
    {
        var decision = Decide(
            Event(WorkScopeProjectionStatus.Running),
            Scope(status: "Created", isHold: true));

        decision.Disposition.Should().Be(WorkScopeProjectionDisposition.Apply);
        decision.ReasonCode.Should().Be("cleaner.running.converge");
        decision.Effects.Select(effect => effect.Action).Should().Equal(
            WorkScopeAction.Release,
            WorkScopeAction.ReleaseHold,
            WorkScopeAction.Start);
        decision.Effects.Should().OnlyContain(effect => effect.CarrierId == null,
            "the pair aggregate is correlated to both carriers by immutable normalized evidence");
        AssertBoundedMetadata(decision);
    }

    [Fact]
    public void Running_AlreadyStarted_ObservesWithoutInventingAnOperation()
    {
        var decision = Decide(
            Event(WorkScopeProjectionStatus.Running),
            Scope(status: "Started"));

        decision.Disposition.Should().Be(WorkScopeProjectionDisposition.Observe);
        decision.ReasonCode.Should().Be("cleaner.running.already-started");
        decision.Effects.Should().BeEmpty();
    }

    [Fact]
    public void RecoveryRequired_HoldsANonterminalScope_AndReplayObserves()
    {
        var evidence = Event(WorkScopeProjectionStatus.RecoveryRequired);

        var apply = Decide(evidence, Scope(status: "Started"));
        var replay = Decide(evidence, Scope(status: "Started", isHold: true));

        apply.Disposition.Should().Be(WorkScopeProjectionDisposition.Apply);
        apply.Effects.Select(effect => effect.Action).Should().Equal(WorkScopeAction.Hold);
        replay.Disposition.Should().Be(WorkScopeProjectionDisposition.Observe);
        replay.ReasonCode.Should().Be("cleaner.recovery.already-held");
    }

    [Theory]
    [InlineData(WorkScopeProjectionStatus.Completed)]
    [InlineData(WorkScopeProjectionStatus.Abandoned)]
    public void TerminalEvidenceBeforeCleanup_HoldsButNeverTerminates(
        WorkScopeProjectionStatus status)
    {
        var evidence = Event(status, terminalCleanupCompleted: false);

        var apply = Decide(evidence, Scope(status: "Started"));
        var replay = Decide(evidence, Scope(status: "Started", isHold: true));

        apply.Disposition.Should().Be(WorkScopeProjectionDisposition.Apply);
        apply.Effects.Select(effect => effect.Action).Should().Equal(WorkScopeAction.Hold);
        apply.Effects.Should().NotContain(effect =>
            effect.Action == WorkScopeAction.Complete || effect.Action == WorkScopeAction.Cancel);
        replay.Disposition.Should().Be(WorkScopeProjectionDisposition.Observe);
        replay.Effects.Should().BeEmpty();
    }

    [Fact]
    public void CompletedAfterCleanup_ConvergesAndReportsTwoCleanCarriers()
    {
        var decision = Decide(
            Event(WorkScopeProjectionStatus.Completed, terminalCleanupCompleted: true),
            Scope(status: "Created", isHold: true));

        decision.Disposition.Should().Be(WorkScopeProjectionDisposition.Apply);
        decision.Effects.Select(effect => effect.Action).Should().Equal(
            WorkScopeAction.Release,
            WorkScopeAction.ReleaseHold,
            WorkScopeAction.Start,
            WorkScopeAction.Complete);
        var completion = decision.Effects[^1];
        completion.GoodQty.Should().Be(2m);
        completion.DefectQty.Should().Be(0m);
        completion.CarrierId.Should().BeNull();
        completion.ResultCode.Should().Be("CLEAN_OK");
        AssertBoundedMetadata(decision);
    }

    [Fact]
    public void AbandonedAfterCleanup_CancelsWithoutReleaseOrStart()
    {
        var decision = Decide(
            Event(WorkScopeProjectionStatus.Abandoned, terminalCleanupCompleted: true),
            Scope(status: "Created", isHold: true));

        decision.Disposition.Should().Be(WorkScopeProjectionDisposition.Apply);
        decision.Effects.Select(effect => effect.Action).Should().Equal(WorkScopeAction.Cancel);
    }

    [Theory]
    [InlineData(WorkScopeProjectionStatus.Completed, "Completed")]
    [InlineData(WorkScopeProjectionStatus.Abandoned, "Cancelled")]
    public void PreexistingMatchingTerminalScope_IsQuarantinedBecauseThisEventHasNoExecutionProvenance(
        WorkScopeProjectionStatus evidenceStatus,
        string scopeStatus)
    {
        var decision = Decide(
            Event(evidenceStatus, terminalCleanupCompleted: true),
            Scope(status: scopeStatus));

        decision.Disposition.Should().Be(WorkScopeProjectionDisposition.Quarantine);
        decision.ReasonCode.Should().Be("cleaner.terminal.preexisting");
        decision.Effects.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkScopeProjectionStatus.Completed, true, "Cancelled")]
    [InlineData(WorkScopeProjectionStatus.Abandoned, true, "Completed")]
    [InlineData(WorkScopeProjectionStatus.Running, false, "Completed")]
    [InlineData(WorkScopeProjectionStatus.Completed, false, "Completed")]
    public void ConflictingTerminalState_IsQuarantined(
        WorkScopeProjectionStatus evidenceStatus,
        bool cleanup,
        string scopeStatus)
    {
        var decision = Decide(Event(evidenceStatus, cleanup), Scope(status: scopeStatus));

        decision.Disposition.Should().Be(WorkScopeProjectionDisposition.Quarantine);
        decision.ReasonCode.Should().Be("cleaner.terminal.preexisting");
        decision.Effects.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Lot")]
    [InlineData("Carrier")]
    [InlineData("Equipment")]
    [InlineData("Batch")]
    public void NonPairScope_IsQuarantinedInsteadOfInventingPairOrLotSemantics(string scopeType)
    {
        var decision = Decide(
            Event(WorkScopeProjectionStatus.Running),
            Scope(scopeType: scopeType));

        decision.Disposition.Should().Be(WorkScopeProjectionDisposition.Quarantine);
        decision.ReasonCode.Should().Be("cleaner.scope.pair-required");
    }

    [Fact]
    public void PairScope_MustMatchRunQuantityAndRecipe()
    {
        var targetMismatch = Decide(
            Event(WorkScopeProjectionStatus.Running),
            Scope(targetId: "pair-other"));
        var quantityMismatch = Decide(
            Event(WorkScopeProjectionStatus.Running),
            Scope(planQty: 1m));
        var recipeMismatch = Decide(
            Event(WorkScopeProjectionStatus.Running),
            Scope(recipeId: "RECIPE-OTHER"));
        var wrongCount = Decide(
            Event(WorkScopeProjectionStatus.Running) with
            {
                Carriers = [new WorkScopeProjectionCarrierDto("Front", "CARRIER-A", "CLEAN-A")],
            },
            Scope());

        targetMismatch.ReasonCode.Should().Be("cleaner.scope.pair-run-mismatch");
        quantityMismatch.ReasonCode.Should().Be("cleaner.scope.pair-quantity-invalid");
        recipeMismatch.ReasonCode.Should().Be("cleaner.scope.recipe-mismatch");
        wrongCount.Disposition.Should().Be(WorkScopeProjectionDisposition.Quarantine);
        wrongCount.ReasonCode.Should().Be("cleaner.evidence.carrier-count-invalid");
    }

    [Fact]
    public void PairScope_RequiresCleanerProcessWithoutWorkOrderOrProductAndARecipeVersion()
    {
        var wrongProcess = Decide(
            Event(WorkScopeProjectionStatus.Running),
            Scope(processId: "ASSEMBLY"));
        var workOrder = Decide(
            Event(WorkScopeProjectionStatus.Running),
            Scope(workOrderId: "WO-01"));
        var product = Decide(
            Event(WorkScopeProjectionStatus.Running),
            Scope(productId: "PRODUCT-01"));
        var missingRecipeVersion = Decide(
            Event(WorkScopeProjectionStatus.Running),
            Scope(recipeVersion: null));

        wrongProcess.ReasonCode.Should().Be("cleaner.scope.process-invalid");
        workOrder.ReasonCode.Should().Be("cleaner.scope.work-order-not-allowed");
        product.ReasonCode.Should().Be("cleaner.scope.product-not-allowed");
        missingRecipeVersion.ReasonCode.Should().Be("cleaner.scope.recipe-version-required");
    }

    [Fact]
    public void PairEvidence_RequiresFrontRearAndDistinctCleaningRuns()
    {
        var invalidLanes = Decide(
            Event(WorkScopeProjectionStatus.Running) with
            {
                Carriers =
                [
                    new WorkScopeProjectionCarrierDto("Left", "CARRIER-A", "CLEAN-A"),
                    new WorkScopeProjectionCarrierDto("Right", "CARRIER-B", "CLEAN-B"),
                ],
            },
            Scope());
        var duplicateRun = Decide(
            Event(WorkScopeProjectionStatus.Running) with
            {
                Carriers =
                [
                    new WorkScopeProjectionCarrierDto("Front", "CARRIER-A", "CLEAN-A"),
                    new WorkScopeProjectionCarrierDto("Rear", "CARRIER-B", "clean-a"),
                ],
            },
            Scope());

        invalidLanes.ReasonCode.Should().Be("cleaner.evidence.lanes-invalid");
        duplicateRun.ReasonCode.Should().Be("cleaner.evidence.cleaning-run-duplicate");
    }

    [Fact]
    public void OversizedSourceMetadata_IsCompactedDeterministicallyWithinStorageLimit()
    {
        var sourceMetadata = JsonSerializer.Serialize(new { trace = new string('x', 12_000) });
        var context = new WorkScopeProjectionContext(
            Event(WorkScopeProjectionStatus.Completed, true) with
            {
                ResultMetadataJson = sourceMetadata,
            },
            Scope(status: "Started"));

        var first = _sut.Decide(context);
        var second = _sut.Decide(context);

        first.Should().BeEquivalentTo(second);
        first.Disposition.Should().Be(WorkScopeProjectionDisposition.Apply);
        AssertBoundedMetadata(first);
        first.Effects.Single().ResultMetadataJson.Should().Contain("sourceResultMetadataSha256");
    }

    [Fact]
    public void InvalidSourceMetadata_IsQuarantined()
    {
        var decision = Decide(
            Event(WorkScopeProjectionStatus.Completed, true) with
            {
                ResultMetadataJson = "{not-json",
            },
            Scope(status: "Started"));

        decision.Disposition.Should().Be(WorkScopeProjectionDisposition.Quarantine);
        decision.ReasonCode.Should().Be("cleaner.evidence.result-metadata-invalid");
    }

    [Fact]
    public void ResultCodeBeyondExecutionStorageLimit_IsQuarantined()
    {
        var decision = Decide(
            Event(WorkScopeProjectionStatus.Completed, true) with
            {
                ResultCode = new string('R', 51),
            },
            Scope(status: "Started"));

        decision.Disposition.Should().Be(WorkScopeProjectionDisposition.Quarantine);
        decision.ReasonCode.Should().Be("cleaner.evidence.result-code-too-long");
    }

    private WorkScopeProjectionDecision Decide(
        WorkScopeProjectionEventDto evidence,
        WorkScopeDto scope) => _sut.Decide(new WorkScopeProjectionContext(evidence, scope));

    private static WorkScopeProjectionEventDto Event(
        WorkScopeProjectionStatus status,
        bool terminalCleanupCompleted = false) => new(
        SourceClientId: "cleaner-client-01",
        EventId: "event-0001",
        RequestHash: new string('a', 64),
        WorkScopeId: "SCOPE-01",
        EquipmentId: "CLEANER-01",
        OperationKey: "operation-01",
        PairRunId: "pair-01",
        SequenceRunId: "sequence-01",
        Status: status,
        TerminalCleanupCompleted: terminalCleanupCompleted,
        RecipeId: "RECIPE-01",
        RecipeSnapshotHash: new string('b', 64),
        ProgramHash: new string('c', 64),
        Carriers:
        [
            new WorkScopeProjectionCarrierDto("Front", "CARRIER-A", "CLEAN-A"),
            new WorkScopeProjectionCarrierDto("Rear", "CARRIER-B", "CLEAN-B"),
        ],
        OccurredAt: new DateTimeOffset(2026, 8, 30, 1, 2, 3, TimeSpan.Zero),
        AcceptedAt: new DateTimeOffset(2026, 8, 30, 1, 2, 4, TimeSpan.Zero),
        SourceRevision: 7,
        ResultCode: "CLEAN_OK",
        ResultMetadataJson: "{\"temperatureC\":42.5}");

    private static WorkScopeDto Scope(
        string status = "Started",
        bool isHold = false,
        string scopeType = "Other",
        string targetId = "pair-01",
        string? carrierId = null,
        decimal? planQty = 2m,
        string? recipeId = "RECIPE-01",
        int? recipeVersion = 1,
        string? processId = "CLEANING",
        string? workOrderId = null,
        string? productId = null) => new(
        WorkScopeId: "SCOPE-01",
        PlantId: "PLANT-01",
        ScopeType: scopeType,
        TargetId: targetId,
        Name: "Cleaner pair scope",
        ParentScopeId: null,
        EquipmentId: "CLEANER-01",
        ProductId: productId,
        ProcessId: processId,
        RecipeId: recipeId,
        RecipeVersion: recipeVersion,
        PlanQty: planQty,
        StartQty: status is "Started" or "Completed" ? 2m : 0m,
        CompleteQty: status == "Completed" ? 2m : 0m,
        ScrapQty: 0m,
        OwnerId: null,
        Status: status,
        IsHold: isHold,
        StartedAt: status is "Started" or "Completed" ? new DateTime(2026, 8, 30, 1, 0, 0, DateTimeKind.Utc) : null,
        CompletedAt: status == "Completed" ? new DateTime(2026, 8, 30, 1, 3, 0, DateTimeKind.Utc) : null,
        Description: null,
        VersionNo: 4,
        CreatedAt: new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
        CreatedBy: "operator-01",
        UpdatedAt: null,
        UpdatedBy: null,
        WorkOrderId: workOrderId,
        CarrierId: carrierId);

    private static void AssertBoundedMetadata(WorkScopeProjectionDecision decision)
    {
        decision.AuditMetadataJson.Should().NotBeNullOrWhiteSpace();
        decision.AuditMetadataJson!.Length.Should().BeLessThanOrEqualTo(4_000);
        using var audit = JsonDocument.Parse(decision.AuditMetadataJson);

        foreach (var effect in decision.Effects)
        {
            effect.ResultCode.Should().NotBeNull();
            effect.ResultCode!.Length.Should().BeLessThanOrEqualTo(50);
            effect.ResultMetadataJson.Should().NotBeNullOrWhiteSpace();
            effect.ResultMetadataJson!.Length.Should().BeLessThanOrEqualTo(4_000);
            using var metadata = JsonDocument.Parse(effect.ResultMetadataJson);
        }
    }
}

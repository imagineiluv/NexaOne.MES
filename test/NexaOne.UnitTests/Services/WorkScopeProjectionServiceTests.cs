using NexaOne.Common;
using NexaOne.POM.Application.WorkScopes;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.UnitTests.Services;

public sealed class WorkScopeProjectionServiceTests
{
    [Fact]
    public async Task Ingest_preserves_the_persisted_payload_and_typed_request_hash()
    {
        var inbox = new RecordingInbox();
        var result = await new WorkScopeProjectionService(inbox).IngestAsync("cleaner-a", Command());

        result.IsSuccess.Should().BeTrue();
        inbox.Envelope!.PayloadJson.Should().Be(
            """{"eventId":"event-1","workScopeId":"WS-1","equipmentId":"EQ-1","operationKey":"clean-pair-1","pairRunId":"pair-1","sequenceRunId":"sequence-1","status":0,"terminalCleanupCompleted":false,"recipeId":"RECIPE-1","recipeSnapshotHash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","programHash":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB","carriers":[{"lane":"front","carrierId":"CARRIER-F","cleaningRunId":"RUN-F"},{"lane":"rear","carrierId":"CARRIER-R","cleaningRunId":"RUN-R"}],"occurredAt":"2026-08-30T00:00:00+00:00","revision":7,"resultCode":"PAIR_RUNNING","resultMetadataJson":null}""");
        inbox.Envelope.RequestHash.Should().Be(
            "6061E30F7C72E9A0E84A7382CD1D61CF6FFF701580A532914BA50079EEB33A8B");
    }

    [Fact]
    public async Task Trimmed_identifiers_hash_case_carrier_order_and_timestamp_offset_keep_the_same_receipt_input()
    {
        var command = Command();
        var baseline = new RecordingInbox();
        await new WorkScopeProjectionService(baseline).IngestAsync("cleaner-a", command);
        var equivalent = command with
        {
            ClientId = " cleaner-a\t",
            EventId = " event-1 ",
            WorkScopeId = "\tWS-1\n",
            EquipmentId = " EQ-1 ",
            OperationKey = " clean-pair-1 ",
            PairRunId = " pair-1 ",
            SequenceRunId = " sequence-1 ",
            RecipeId = " RECIPE-1 ",
            RecipeSnapshotHash = " " + command.RecipeSnapshotHash.ToUpperInvariant() + "\n",
            ProgramHash = "\t" + command.ProgramHash.ToUpperInvariant() + " ",
            ResultCode = " PAIR_RUNNING ",
            OccurredAt = command.OccurredAt.ToOffset(TimeSpan.FromHours(9)),
            Carriers =
            [
                new(" REAR ", " CARRIER-R ", " RUN-R "),
                new(" FRONT ", " CARRIER-F ", " RUN-F "),
            ],
        };
        var inbox = new RecordingInbox();

        var result = await new WorkScopeProjectionService(inbox).IngestAsync(" cleaner-a ", equivalent);

        result.IsSuccess.Should().BeTrue();
        inbox.Envelope.Should().BeEquivalentTo(baseline.Envelope);
    }

    [Theory]
    [InlineData("ClientId", 100, "ClientId")]
    [InlineData("EventId", 200, "Projection.Identity")]
    [InlineData("WorkScopeId", 50, "Projection.Identity")]
    [InlineData("EquipmentId", 100, "Projection.Identity")]
    [InlineData("OperationKey", 200, "Projection.Identity")]
    [InlineData("PairRunId", 100, "Projection.Identity")]
    [InlineData("SequenceRunId", 100, "Projection.Identity")]
    [InlineData("RecipeId", 100, "Projection.Identity")]
    [InlineData("ResultCode", 100, "Projection.Identity")]
    [InlineData("Lane", 30, "Carriers")]
    [InlineData("CarrierId", 100, "Carriers")]
    [InlineData("CleaningRunId", 100, "Carriers")]
    public async Task Identifier_caps_are_inclusive_after_product_normalization(
        string field, int maximum, string errorCode)
    {
        var fitting = ChangeIdentifier(Command(), field, " " + new string('X', maximum) + " ");
        var inbox = new RecordingInbox();
        (await new WorkScopeProjectionService(inbox).IngestAsync(fitting.ClientId, fitting))
            .IsSuccess.Should().BeTrue();
        inbox.Calls.Should().Be(1);

        var oversized = ChangeIdentifier(Command(), field, new string('X', maximum + 1));
        await AssertRejected(oversized.ClientId, oversized, errorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    public async Task Missing_or_internal_control_identifiers_return_existing_validation_errors(string? value)
    {
        foreach (var field in new[] { "EventId", "WorkScopeId", "EquipmentId", "OperationKey", "PairRunId", "SequenceRunId", "RecipeId", "ResultCode" })
            await AssertRejected("cleaner-a", ChangeIdentifier(Command(), field, value!), "Projection.Identity");

        foreach (var field in new[] { "Lane", "CarrierId", "CleaningRunId" })
            await AssertRejected("cleaner-a", ChangeIdentifier(Command(), field, value!), "Carriers");

        await AssertRejected(value!, Command() with { ClientId = value! }, "ClientId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public async Task Nonpositive_revision_is_rejected_before_persistence(long revision)
        => await AssertRejected("cleaner-a", Command() with { Revision = revision }, "Revision");

    [Theory]
    [InlineData(1)]
    [InlineData(long.MaxValue)]
    public async Task Positive_revision_keeps_its_original_value(long revision)
    {
        var inbox = new RecordingInbox();
        (await new WorkScopeProjectionService(inbox).IngestAsync("cleaner-a", Command() with { Revision = revision }))
            .IsSuccess.Should().BeTrue();
        inbox.Envelope!.SourceRevision.Should().Be(revision);
    }

    [Fact]
    public async Task Scalar_validation_keeps_error_precedence_and_source_authority()
    {
        var invalid = Command() with { Status = (WorkScopeProjectionStatus)99, Revision = 0, OccurredAt = default };
        await AssertRejected("other-client", invalid, "ClientId");
        await AssertRejected("cleaner-a", invalid, "Status");
        await AssertRejected("cleaner-a", invalid with { Status = WorkScopeProjectionStatus.Running }, "Revision");
        await AssertRejected("cleaner-a", invalid with { Status = WorkScopeProjectionStatus.Running, Revision = 1 }, "OccurredAt");
        await AssertRejected("cleaner-a", Command() with { TerminalCleanupCompleted = true }, "TerminalCleanupCompleted");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("G")]
    [InlineData("Ｆ")]
    public async Task Invalid_digest_does_not_escape_as_an_exception_or_reach_the_inbox(string? value)
    {
        var digest = value?.Length == 1 ? new string(value[0], 64) : value;
        await AssertRejected("cleaner-a", Command() with { RecipeSnapshotHash = digest! }, "Projection.Hash");
        await AssertRejected("cleaner-a", Command() with { ProgramHash = digest! }, "Projection.Hash");
        await AssertRejected("cleaner-a", Command() with { ProgramHash = new string('a', 63) }, "Projection.Hash");
        await AssertRejected("cleaner-a", Command() with { ProgramHash = new string('a', 65) }, "Projection.Hash");
    }

    [Fact]
    public async Task Carrier_pair_policy_and_metadata_errors_remain_product_owned()
    {
        var command = Command();
        await AssertRejected("cleaner-a", command with { Carriers = [command.Carriers[0]] }, "Carriers");
        await AssertRejected("cleaner-a", command with { Carriers = [command.Carriers[0], command.Carriers[0]] }, "Carriers");
        await AssertRejected("cleaner-a", command with { ResultMetadataJson = "{" }, "ResultMetadataJson");
        await AssertRejected("cleaner-a", command with { ResultMetadataJson = new string('x', 64_001) }, "ResultMetadataJson");
    }

    private static async Task AssertRejected(string source, WorkScopeProjectionCommand command, string errorCode)
    {
        var inbox = new RecordingInbox();
        var result = await new WorkScopeProjectionService(inbox).IngestAsync(source, command);
        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
        result.Error.Code.Should().Be(errorCode);
        inbox.Calls.Should().Be(0);
    }

    private static WorkScopeProjectionCommand ChangeIdentifier(WorkScopeProjectionCommand command, string field, string value)
        => field switch
        {
            "ClientId" => command with { ClientId = value },
            "EventId" => command with { EventId = value },
            "WorkScopeId" => command with { WorkScopeId = value },
            "EquipmentId" => command with { EquipmentId = value },
            "OperationKey" => command with { OperationKey = value },
            "PairRunId" => command with { PairRunId = value },
            "SequenceRunId" => command with { SequenceRunId = value },
            "RecipeId" => command with { RecipeId = value },
            "ResultCode" => command with { ResultCode = value },
            "Lane" => command with { Carriers = [command.Carriers[0] with { Lane = value }, command.Carriers[1]] },
            "CarrierId" => command with { Carriers = [command.Carriers[0] with { CarrierId = value }, command.Carriers[1]] },
            "CleaningRunId" => command with { Carriers = [command.Carriers[0] with { CleaningRunId = value }, command.Carriers[1]] },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

    private static WorkScopeProjectionCommand Command() => new(
        "cleaner-a", "event-1", "WS-1", "EQ-1", "clean-pair-1", "pair-1", "sequence-1",
        WorkScopeProjectionStatus.Running, false, "RECIPE-1", new string('a', 64), new string('b', 64),
        [new("front", "CARRIER-F", "RUN-F"), new("rear", "CARRIER-R", "RUN-R")],
        new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero), 7, "PAIR_RUNNING");

    private sealed class RecordingInbox : IWorkScopeProjectionInbox
    {
        public int Calls { get; private set; }
        public WorkScopeProjectionEnvelope? Envelope { get; private set; }

        public Task<WorkScopeProjectionPersistResult> PersistAsync(WorkScopeProjectionEnvelope envelope, CancellationToken ct = default)
        {
            Calls++;
            Envelope = envelope;
            return Task.FromResult(new WorkScopeProjectionPersistResult(
                WorkScopeProjectionPersistKind.Accepted, envelope.SourceClientId, envelope.EventId,
                envelope.WorkScopeId, true, envelope.SourceRevision, envelope.OccurredAt.AddSeconds(1)));
        }
    }
}

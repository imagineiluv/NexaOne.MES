using NexaOne.Common;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Pom;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class WorkScopeProjectionAuthorityValidatorProxyTests
{
    [Fact]
    public async Task Resolve_failure_wrong_type_and_self_all_fail_closed()
    {
        var failedResolve = new WorkScopeProjectionAuthorityValidatorProxy(
            () => throw new InvalidOperationException("missing child context"));
        var wrongType = new WorkScopeProjectionAuthorityValidatorProxy(() => new object());
        WorkScopeProjectionAuthorityValidatorProxy? self = null;
        self = new WorkScopeProjectionAuthorityValidatorProxy(() => self);

        foreach (var proxy in new[] { failedResolve, wrongType, self })
        {
            var result = await proxy.ValidateAsync(Command());
            Assert.False(result.IsAccepted);
            Assert.Equal("Projection.Authority.ValidatorUnavailable", result.RejectionCode);
        }
    }

    [Fact]
    public async Task Every_call_resolves_the_current_target_instead_of_caching_it()
    {
        var calls = 0;
        var first = new RejectingTarget("target.one");
        var second = new RejectingTarget("target.two");
        var proxy = new WorkScopeProjectionAuthorityValidatorProxy(
            () => ++calls == 1 ? first : second);

        var firstResult = await proxy.ValidateAsync(Command());
        var secondResult = await proxy.ValidateAsync(Command());

        Assert.Equal(2, calls);
        Assert.Equal("target.one", firstResult.RejectionCode);
        Assert.Equal("target.two", secondResult.RejectionCode);
    }

    [Fact]
    public void Synchronous_delegated_exception_is_not_converted_to_unavailable()
    {
        var proxy = new WorkScopeProjectionAuthorityValidatorProxy(
            () => new SynchronouslyThrowingTarget());

        var error = Assert.Throws<InvalidOperationException>(
            (Action)(() => _ = proxy.ValidateAsync(Command())));

        Assert.Equal("delegated failure", error.Message);
    }

    [Fact]
    public async Task Asynchronous_delegated_exception_and_cancellation_are_not_swallowed()
    {
        var failed = new WorkScopeProjectionAuthorityValidatorProxy(
            () => new AsynchronouslyThrowingTarget());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => failed.ValidateAsync(Command()));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = new WorkScopeProjectionAuthorityValidatorProxy(
            () => new CancelingTarget());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.ValidateAsync(Command(), cancellation.Token));
    }

    [Fact]
    public async Task Legacy_target_is_adapted_on_each_call_without_being_cached()
    {
        var calls = 0;
        var first = new LegacyTarget(Result.Failure<WorkScopeProjectionAuthorityEvidence>(
            Error.Conflict("legacy.one", "first legacy target")));
        var second = new LegacyTarget(Result.Success(Evidence()));
        var proxy = new WorkScopeProjectionAuthorityValidatorProxy(
            () => ++calls == 1 ? first : second);

        var rejected = await proxy.ValidateAsync(Command());
        var accepted = await proxy.ValidateAsync(Command());

        Assert.Equal(2, calls);
        Assert.Equal("legacy.one", rejected.RejectionCode);
        Assert.True(accepted.IsAccepted);
        Assert.Equal("WS-1", accepted.Evidence!.WorkScopeId);
    }

    [Fact]
    public async Task Invalid_legacy_outcomes_fail_closed_but_delegated_failures_propagate()
    {
        var blankFailure = new WorkScopeProjectionAuthorityValidatorProxy(
            () => new LegacyTarget(Result.Failure<WorkScopeProjectionAuthorityEvidence>(Error.None)));
        var nullSuccess = new WorkScopeProjectionAuthorityValidatorProxy(
            () => new LegacyTarget(Result.Success<WorkScopeProjectionAuthorityEvidence>(null!)));

        foreach (var proxy in new[] { blankFailure, nullSuccess })
        {
            var result = await proxy.ValidateAsync(Command());
            Assert.False(result.IsAccepted);
            Assert.Equal("Projection.Authority.InvalidValidatorDecision", result.RejectionCode);
        }

        var synchronous = new WorkScopeProjectionAuthorityValidatorProxy(
            () => new SynchronouslyThrowingLegacyTarget());
        Assert.Throws<InvalidOperationException>(
            (Action)(() => _ = synchronous.ValidateAsync(Command())));

        var asynchronous = new WorkScopeProjectionAuthorityValidatorProxy(
            () => new AsynchronouslyThrowingLegacyTarget());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => asynchronous.ValidateAsync(Command()));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = new WorkScopeProjectionAuthorityValidatorProxy(
            () => new CancelingLegacyTarget());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.ValidateAsync(Command(), cancellation.Token));
    }

    private static WorkScopeProjectionAuthorityProvisionCommand Command() => new(
        "WS-1",
        "cleaner-a",
        "EQ-1",
        "CLEANING",
        "PAIR-1",
        "SEQ-1",
        "EXEC-1",
        "ART-1",
        "idem-1",
        "operator");

    private static WorkScopeProjectionAuthorityEvidence Evidence() => new(
        "WS-1", "cleaner-a", "EQ-1", "CLEANING", "PAIR-1", "SEQ-1", "EXEC-1",
        "RCP-1", 1, "recipe/v1", new string('A', 64), "ART-1", "program/v1",
        new string('B', 64));

    private sealed class RejectingTarget(string code) : IWorkScopeProjectionAuthorityValidatorV2
    {
        public Task<WorkScopeProjectionAuthorityValidationDecision> ValidateAsync(
            WorkScopeProjectionAuthorityProvisionCommand command,
            CancellationToken ct = default) => Task.FromResult(
            WorkScopeProjectionAuthorityValidationDecision.Rejected(code, "test target"));
    }

    private sealed class SynchronouslyThrowingTarget : IWorkScopeProjectionAuthorityValidatorV2
    {
        public Task<WorkScopeProjectionAuthorityValidationDecision> ValidateAsync(
            WorkScopeProjectionAuthorityProvisionCommand command,
            CancellationToken ct = default) => throw new InvalidOperationException("delegated failure");
    }

    private sealed class AsynchronouslyThrowingTarget : IWorkScopeProjectionAuthorityValidatorV2
    {
        public Task<WorkScopeProjectionAuthorityValidationDecision> ValidateAsync(
            WorkScopeProjectionAuthorityProvisionCommand command,
            CancellationToken ct = default) => Task.FromException<WorkScopeProjectionAuthorityValidationDecision>(
            new InvalidOperationException("delegated async failure"));
    }

    private sealed class CancelingTarget : IWorkScopeProjectionAuthorityValidatorV2
    {
        public Task<WorkScopeProjectionAuthorityValidationDecision> ValidateAsync(
            WorkScopeProjectionAuthorityProvisionCommand command,
            CancellationToken ct = default) => Task.FromCanceled<WorkScopeProjectionAuthorityValidationDecision>(ct);
    }

    private sealed class LegacyTarget(Result<WorkScopeProjectionAuthorityEvidence> result)
        : IWorkScopeProjectionAuthorityValidator
    {
        public Task<Result<WorkScopeProjectionAuthorityEvidence>> ValidateAsync(
            WorkScopeProjectionAuthorityProvisionCommand command,
            CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class SynchronouslyThrowingLegacyTarget : IWorkScopeProjectionAuthorityValidator
    {
        public Task<Result<WorkScopeProjectionAuthorityEvidence>> ValidateAsync(
            WorkScopeProjectionAuthorityProvisionCommand command,
            CancellationToken ct = default) => throw new InvalidOperationException("legacy delegated failure");
    }

    private sealed class AsynchronouslyThrowingLegacyTarget : IWorkScopeProjectionAuthorityValidator
    {
        public Task<Result<WorkScopeProjectionAuthorityEvidence>> ValidateAsync(
            WorkScopeProjectionAuthorityProvisionCommand command,
            CancellationToken ct = default) => Task.FromException<Result<WorkScopeProjectionAuthorityEvidence>>(
            new InvalidOperationException("legacy delegated async failure"));
    }

    private sealed class CancelingLegacyTarget : IWorkScopeProjectionAuthorityValidator
    {
        public Task<Result<WorkScopeProjectionAuthorityEvidence>> ValidateAsync(
            WorkScopeProjectionAuthorityProvisionCommand command,
            CancellationToken ct = default) => Task.FromCanceled<Result<WorkScopeProjectionAuthorityEvidence>>(ct);
    }
}

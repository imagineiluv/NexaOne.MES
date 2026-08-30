using NexaOne.Common;

namespace NexaOne.ServiceContracts.Pom;

/// <summary>
/// Adapts the committed legacy validator contract to the contract-owned V2 decision. It does not
/// catch delegated exceptions or cancellation; only malformed legacy outcomes fail closed.
/// </summary>
public sealed class LegacyWorkScopeProjectionAuthorityValidatorAdapter
    : IWorkScopeProjectionAuthorityValidatorV2
{
    private readonly IWorkScopeProjectionAuthorityValidator _legacy;

    public LegacyWorkScopeProjectionAuthorityValidatorAdapter(
        IWorkScopeProjectionAuthorityValidator legacy) =>
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));

    public Task<WorkScopeProjectionAuthorityValidationDecision> ValidateAsync(
        WorkScopeProjectionAuthorityProvisionCommand command,
        CancellationToken ct = default)
    {
        // Invoke before entering the async mapper so a synchronous delegated exception remains a
        // synchronous delegated exception instead of being mistaken for an adapter failure.
        var pending = _legacy.ValidateAsync(command, ct);
        return pending is null ? Invalid() : AdaptAsync(pending);
    }

    private static async Task<WorkScopeProjectionAuthorityValidationDecision> AdaptAsync(
        Task<Result<WorkScopeProjectionAuthorityEvidence>> pending)
    {
        var result = await pending.ConfigureAwait(false);
        if (result is null) return InvalidDecision();
        if (result.IsSuccess)
        {
            var evidence = result.Value;
            return evidence is null
                ? InvalidDecision()
                : WorkScopeProjectionAuthorityValidationDecision.Accepted(evidence);
        }

        var error = result.Error;
        return error is not null
            && !string.IsNullOrWhiteSpace(error.Code)
            && !string.IsNullOrWhiteSpace(error.Description)
            ? WorkScopeProjectionAuthorityValidationDecision.Rejected(error.Code, error.Description)
            : InvalidDecision();
    }

    private static Task<WorkScopeProjectionAuthorityValidationDecision> Invalid() =>
        Task.FromResult(InvalidDecision());

    private static WorkScopeProjectionAuthorityValidationDecision InvalidDecision() =>
        WorkScopeProjectionAuthorityValidationDecision.Rejected(
            "Projection.Authority.InvalidValidatorDecision",
            "The legacy authority validator returned an invalid result.");
}

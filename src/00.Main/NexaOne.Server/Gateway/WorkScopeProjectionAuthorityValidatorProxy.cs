using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

/// <summary>
/// Resolves the product-owned authority validator from the current POM child context for every
/// call. The target is deliberately not cached because <c>ApplicationServer.ReloadService</c>
/// replaces and disposes the service context.
/// </summary>
public sealed class WorkScopeProjectionAuthorityValidatorProxy
    : IWorkScopeProjectionAuthorityValidatorV2
{
    internal const string TargetModule = "Pom";
    internal const string TargetBean = "workScopeProjectionAuthorityValidator";

    private readonly Func<object?> _resolveTarget;

    public WorkScopeProjectionAuthorityValidatorProxy(ModuleBeanResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolveTarget = () => resolver.Resolve<object>(TargetModule, TargetBean);
    }

    internal WorkScopeProjectionAuthorityValidatorProxy(Func<object?> resolveTarget) =>
        _resolveTarget = resolveTarget ?? throw new ArgumentNullException(nameof(resolveTarget));

    public Task<WorkScopeProjectionAuthorityValidationDecision> ValidateAsync(
        WorkScopeProjectionAuthorityProvisionCommand command,
        CancellationToken ct = default)
    {
        object? candidate;
        try
        {
            candidate = _resolveTarget();
        }
        catch (Exception)
        {
            return Unavailable();
        }

        if (ReferenceEquals(candidate, this))
        {
            return Unavailable();
        }

        // Do not catch here. A resolved validator's DB, cancellation and application failures are
        // real delegated-call failures and must remain visible to the caller.
        return candidate switch
        {
            IWorkScopeProjectionAuthorityValidatorV2 target => target.ValidateAsync(command, ct),
            IWorkScopeProjectionAuthorityValidator legacy =>
                new LegacyWorkScopeProjectionAuthorityValidatorAdapter(legacy)
                    .ValidateAsync(command, ct),
            _ => Unavailable(),
        };
    }

    private static Task<WorkScopeProjectionAuthorityValidationDecision> Unavailable() =>
        Task.FromResult(WorkScopeProjectionAuthorityValidationDecision.Rejected(
            "Projection.Authority.ValidatorUnavailable",
            "A trusted product projection authority validator is unavailable."));
}

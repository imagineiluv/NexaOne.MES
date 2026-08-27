using NexaOne.ServiceContracts.Qms;

namespace NexaOne.Server.Gateway;

/// <summary>
/// Parent-context proxy required because POM and QMS are sibling Spring plugin contexts. Business
/// policy and SQL remain in QMS; this host adapter only resolves and delegates through the shared contract.
/// </summary>
public sealed class ProductionQualityGatewayProxy : IProductionQualityGateway
{
    private readonly ModuleBeanResolver _resolver;

    public ProductionQualityGatewayProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<ProductionQualityGateResult> EvaluateAsync(
        string lotId,
        string processId,
        string? workOrderId,
        CancellationToken ct = default)
    {
        return _resolver.Resolve<IProductionQualityGateway>(
                "Qms", "qmsProductionQualityGateway")
            .EvaluateAsync(lotId, processId, workOrderId, ct);
    }
}

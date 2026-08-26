using NexaOne.ServiceContracts.Qms;
using NexaFramework;

namespace NexaOne.Server.Gateway;

/// <summary>
/// Parent-context proxy required because POM and QMS are sibling Spring plugin contexts. Business
/// policy and SQL remain in QMS; this host adapter only resolves and delegates through the shared contract.
/// </summary>
public sealed class ProductionQualityGatewayProxy : IProductionQualityGateway
{
    public Task<ProductionQualityGateResult> EvaluateAsync(
        string lotId,
        string processId,
        string? workOrderId,
        CancellationToken ct = default)
    {
        var bean = ApplicationServer.GetInstance().GetBean(
            "Qms",
            "qmsProductionQualityGateway");
        if (bean is not IProductionQualityGateway gateway)
        {
            throw new InvalidOperationException(
                $"Module bridge bean 'Qms/qmsProductionQualityGateway' is "
                + $"'{bean.GetType().FullName}', not '{typeof(IProductionQualityGateway).FullName}'.");
        }

        return gateway.EvaluateAsync(lotId, processId, workOrderId, ct);
    }
}

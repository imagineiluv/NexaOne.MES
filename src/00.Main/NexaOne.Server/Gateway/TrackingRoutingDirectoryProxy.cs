using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.Server.Gateway;

/// <summary>MDM 라우팅 directory를 POM 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class TrackingRoutingDirectoryProxy : ITrackingRoutingDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public TrackingRoutingDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<TrackingProductRouting?> GetProductRoutingAsync(
        string routingId,
        CancellationToken ct = default)
        => _resolver.Resolve<ITrackingRoutingDirectory>("Mdm", "trackingRoutingDirectory")
            .GetProductRoutingAsync(routingId, ct);
}

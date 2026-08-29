using NexaOne.ServiceContracts.Qms;

namespace NexaOne.Server.Gateway;

/// <summary>QMS 불량 코드 directory를 POM 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class TrackingDefectDirectoryProxy : ITrackingDefectDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public TrackingDefectDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<bool> IsValidAsync(string defectCode, CancellationToken ct = default)
        => _resolver.Resolve<ITrackingDefectDirectory>("Qms", "trackingDefectDirectory")
            .IsValidAsync(defectCode, ct);
}

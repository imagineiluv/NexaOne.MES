using NexaOne.Common;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.Server.Gateway;

/// <summary>
/// API/부모 Spring 컨텍스트에서 IVT 소유 TRACE 자재 명령 경계로 위임하는 축소 프록시입니다.
/// </summary>
public sealed class TraceMaterialBridgeProxy : ITraceMaterialBridge
{
    private readonly ModuleBeanResolver _resolver;

    public TraceMaterialBridgeProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<Result<TraceBindingDto>> ExecuteBindingAsync(
        TraceBindingCommand command,
        CancellationToken ct = default) => Resolve().ExecuteBindingAsync(command, ct);

    public Task<Result<FeedSessionDto>> ExecuteFeedSessionAsync(
        FeedSessionCommand command,
        CancellationToken ct = default) => Resolve().ExecuteFeedSessionAsync(command, ct);

    private ITraceMaterialBridge Resolve() =>
        _resolver.Resolve<ITraceMaterialBridge>("Ivt", "traceMaterialBridge");
}

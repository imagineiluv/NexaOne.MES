using NexaOne.Common;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Application.Materials;

/// <summary>TRACE binding 설정과 물리 자재 장착 세션을 IVT 조립 경계 하나로 노출합니다.</summary>
public sealed class TraceMaterialBridge : ITraceMaterialBridge
{
    private readonly TraceBindingService _bindings;
    private readonly FeedSessionService _feedSessions;
    private readonly bool _bindingsEnabled;
    private readonly bool _feedSessionsEnabled;

    internal TraceMaterialBridge(
        TraceBindingService bindings,
        FeedSessionService feedSessions,
        bool bindingsEnabled,
        bool feedSessionsEnabled)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _feedSessions = feedSessions ?? throw new ArgumentNullException(nameof(feedSessions));
        _bindingsEnabled = bindingsEnabled;
        _feedSessionsEnabled = feedSessionsEnabled;
    }

    public Task<Result<TraceBindingDto>> ExecuteBindingAsync(
        TraceBindingCommand command,
        CancellationToken ct = default) => _bindingsEnabled
            ? _bindings.ExecuteAsync(command, ct)
            : Task.FromResult(Result.Failure<TraceBindingDto>(Error.Conflict(
                "IVT.TraceBinding.FeatureDisabled",
                "TRACE binding mutation is disabled until a durable cross-process maintenance fence is implemented.")));

    public Task<Result<FeedSessionDto>> ExecuteFeedSessionAsync(
        FeedSessionCommand command,
        CancellationToken ct = default) => _feedSessionsEnabled
            ? _feedSessions.ExecuteAsync(command, ct)
            : Task.FromResult(Result.Failure<FeedSessionDto>(Error.Conflict(
                "IVT.FeedSession.FeatureDisabled",
                "Feed-session mutation is disabled until durable TRACE drain finalization is available.")));
}

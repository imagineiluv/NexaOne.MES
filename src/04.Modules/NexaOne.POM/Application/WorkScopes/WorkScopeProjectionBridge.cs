using NexaOne.Common;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Application.WorkScopes;

/// <summary>공유 transport contract와 POM projection ingestion module 사이의 얇은 adapter입니다.</summary>
internal sealed class WorkScopeProjectionBridge : IWorkScopeProjectionBridge
{
    private readonly WorkScopeProjectionService _service;

    public WorkScopeProjectionBridge(WorkScopeProjectionService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    public Task<Result<WorkScopeProjectionReceiptDto>> IngestAsync(
        string sourceClientId,
        WorkScopeProjectionCommand command,
        CancellationToken ct = default) => _service.IngestAsync(sourceClientId, command, ct);
}

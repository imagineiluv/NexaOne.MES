using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaFramework.Service;
using NexaFramework.Service.Crm;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Crm;

namespace NexaOne.Server.Gateway;

[ApiController]
[Authorize]
[Route("api/v1/crm/{tenantId:guid}/{organizationId:guid}")]
public sealed class CrmController(ICrmBridge bridge, ILogger<CrmController> logger) : ControllerBase
{
    [HttpGet("pipelines")]
    public Task<IActionResult> ListPipelines(Guid tenantId, Guid organizationId,
        [FromQuery] PipelineQuery query, CancellationToken ct)
        => Execute(user => bridge.ListPipelinesAsync(user, tenantId, organizationId, query, ct));

    [HttpGet("pipelines/{id:guid}")]
    public Task<IActionResult> GetPipeline(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetPipelineAsync(user, tenantId, organizationId, id, ct));

    [HttpPost("pipelines")]
    public Task<IActionResult> CreatePipeline(Guid tenantId, Guid organizationId,
        [FromBody] PipelineInput input, CancellationToken ct)
        => Execute(user => bridge.CreatePipelineAsync(user, tenantId, organizationId, input, ct));

    [HttpPut("pipelines/{id:guid}")]
    public Task<IActionResult> UpdatePipeline(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] PipelineChange command, CancellationToken ct)
        => Execute(user => bridge.UpdatePipelineAsync(user, tenantId, organizationId,
            id, command.Version, command.Input, command.Removal, ct));

    [HttpDelete("pipelines/{id:guid}")]
    public Task<IActionResult> DeletePipeline(Guid tenantId, Guid organizationId, Guid id,
        [FromQuery] Guid version, [FromQuery] DealRemoval removal, CancellationToken ct)
        => Execute(async user =>
        {
            await bridge.DeletePipelineAsync(user, tenantId, organizationId, id, version, removal, ct);
            return new Deleted(id);
        });

    [HttpGet("deals")]
    public Task<IActionResult> ListDeals(Guid tenantId, Guid organizationId,
        [FromQuery] DealQuery query, CancellationToken ct)
        => Execute(user => bridge.ListDealsAsync(user, tenantId, organizationId, query, ct));

    [HttpGet("deals/{id:guid}")]
    public Task<IActionResult> GetDeal(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetDealAsync(user, tenantId, organizationId, id, ct));

    [HttpPost("deals")]
    public Task<IActionResult> CreateDeal(Guid tenantId, Guid organizationId,
        [FromBody] DealInput input, CancellationToken ct)
        => Execute(user => bridge.CreateDealAsync(user, tenantId, organizationId, input, ct));

    [HttpPut("deals/{id:guid}")]
    public Task<IActionResult> UpdateDeal(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] DealChange command, CancellationToken ct)
        => Execute(user => bridge.UpdateDealAsync(user, tenantId, organizationId,
            id, command.Version, command.Input, ct));

    [HttpPost("deals/{id:guid}/move")]
    public Task<IActionResult> MoveDeal(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] DealMove command, CancellationToken ct)
        => Execute(user => bridge.MoveDealAsync(user, tenantId, organizationId,
            id, command.Version, command.StageId, ct));

    [HttpDelete("deals/{id:guid}")]
    public Task<IActionResult> DeleteDeal(Guid tenantId, Guid organizationId, Guid id,
        [FromQuery] Guid version, CancellationToken ct)
        => Execute(async user =>
        {
            await bridge.DeleteDealAsync(user, tenantId, organizationId, id, version, ct);
            return new Deleted(id);
        });

    private async Task<IActionResult> Execute<T>(Func<string, Task<T>> action)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        try
        {
            return Ok(await action(userId));
        }
        catch (BusinessException error)
        {
            if (error.Code == "CRM_ACCESS_DENIED") return Forbid();
            var status = error.Code.EndsWith("_NOT_FOUND", StringComparison.Ordinal) ? 404
                : error.Code.StartsWith("INVALID_", StringComparison.Ordinal) ? 400 : 409;
            return StatusCode(status, new { code = error.Code });
        }
        catch (BusinessStorageException error)
        {
            logger.LogError(error, "CRM persistence failed; write outcome may be unknown.");
            return Problem(statusCode: 503, title: "CRM storage is unavailable.");
        }
    }

    public sealed record PipelineChange(Guid Version, PipelineInput Input,
        DealRemoval Removal = DealRemoval.RejectIfReferenced);
    public sealed record DealChange(Guid Version, DealInput Input);
    public sealed record DealMove(Guid Version, Guid StageId);
    public sealed record Deleted(Guid Id);
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaFramework.Service;
using NexaFramework.Service.Crm;
using NexaFramework.Service.Projects;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Crm;

namespace NexaOne.Server.Gateway;

[ApiController]
[Authorize]
[Route("api/v1/crm/{tenantId:guid}/{organizationId:guid}")]
public sealed class CrmController(ICrmBridge bridge, ILogger<CrmController> logger) : ControllerBase
{
    [HttpGet("/api/v1/crm/scopes/me")]
    public Task<IActionResult> ListScopes(CancellationToken ct,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListAccessibleScopesAsync(user, offset, limit, ct));

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

    [HttpGet("projects")]
    public Task<IActionResult> ListProjects(Guid tenantId, Guid organizationId,
        [FromQuery] ProjectQuery query, CancellationToken ct)
        => Execute(user => bridge.ListProjectsAsync(user, tenantId, organizationId, query, ct));

    [HttpGet("projects/{id:guid}")]
    public Task<IActionResult> GetProject(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetProjectAsync(user, tenantId, organizationId, id, ct));

    [HttpPost("projects")]
    public Task<IActionResult> CreateProject(Guid tenantId, Guid organizationId,
        [FromBody] ProjectCreate command, CancellationToken ct)
        => Execute(user => bridge.CreateProjectAsync(user, tenantId, organizationId,
            command.Input, command.Links, ct));

    [HttpPut("projects/{id:guid}")]
    public Task<IActionResult> UpdateProject(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] ProjectChange command, CancellationToken ct)
        => Execute(user => bridge.UpdateProjectAsync(user, tenantId, organizationId,
            id, command.Version, command.Input, ct));

    [HttpPut("projects/{id:guid}/links")]
    public Task<IActionResult> SetProjectLinks(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] ProjectLinksChange command, CancellationToken ct)
        => Execute(user => bridge.SetProjectLinksAsync(user, tenantId, organizationId,
            id, command.Version, command.Links, ct));

    [HttpDelete("projects/{id:guid}")]
    public Task<IActionResult> DeleteProject(Guid tenantId, Guid organizationId, Guid id,
        [FromQuery] Guid version, CancellationToken ct)
        => Execute(async user =>
        {
            await bridge.DeleteProjectAsync(user, tenantId, organizationId, id, version, ct);
            return new Deleted(id);
        });

    [HttpGet("teams")]
    public Task<IActionResult> ListTeams(Guid tenantId, Guid organizationId,
        [FromQuery] TeamQuery query, CancellationToken ct)
        => Execute(user => bridge.ListTeamsAsync(user, tenantId, organizationId, query, ct));

    [HttpGet("teams/{id:guid}")]
    public Task<IActionResult> GetTeam(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetTeamAsync(user, tenantId, organizationId, id, ct));

    [HttpPost("teams")]
    public Task<IActionResult> CreateTeam(Guid tenantId, Guid organizationId,
        [FromBody] TeamCreate command, CancellationToken ct)
        => Execute(user => bridge.CreateTeamAsync(user, tenantId, organizationId,
            command.Input, command.Members, ct));

    [HttpPut("teams/{id:guid}")]
    public Task<IActionResult> UpdateTeam(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] TeamChange command, CancellationToken ct)
        => Execute(user => bridge.UpdateTeamAsync(user, tenantId, organizationId,
            id, command.Version, command.Input, command.Members, ct));

    [HttpDelete("teams/{id:guid}")]
    public Task<IActionResult> DeleteTeam(Guid tenantId, Guid organizationId, Guid id,
        [FromQuery] Guid version, CancellationToken ct)
        => Execute(async user =>
        {
            await bridge.DeleteTeamAsync(user, tenantId, organizationId, id, version, ct);
            return new Deleted(id);
        });

    [HttpGet("customer-enrollments")]
    public Task<IActionResult> ListCustomerEnrollments(Guid tenantId, Guid organizationId,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? text = null,
        CancellationToken ct = default)
        => Execute(user => bridge.ListCustomerEnrollmentsAsync(user, tenantId, organizationId,
            offset, limit, text, ct));

    [HttpPost("customer-enrollments")]
    public Task<IActionResult> EnrollCustomer(Guid tenantId, Guid organizationId,
        [FromBody] CustomerEnrollmentCreate command, CancellationToken ct)
        => Execute(user => bridge.EnrollCustomerAsync(user, tenantId, organizationId, command.CustomerId, ct));

    [HttpDelete("customer-enrollments/{contactId:guid}")]
    public Task<IActionResult> DeleteCustomerEnrollment(Guid tenantId, Guid organizationId,
        Guid contactId, [FromQuery] Guid version, CancellationToken ct)
        => Execute(async user =>
        {
            await bridge.DeleteCustomerEnrollmentAsync(user, tenantId, organizationId,
                contactId, version, ct);
            return new Deleted(contactId);
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
            if (error.Code is "CRM_ACCESS_DENIED" or "WORK_ACCESS_DENIED") return Forbid();
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
    public sealed record ProjectCreate(ProjectInput Input, ProjectLinks Links);
    public sealed record ProjectChange(Guid Version, ProjectInput Input);
    public sealed record ProjectLinksChange(Guid Version, ProjectLinks Links);
    public sealed record TeamCreate(TeamInput Input, IReadOnlyList<ProjectMember> Members);
    public sealed record TeamChange(Guid Version, TeamInput Input, IReadOnlyList<ProjectMember>? Members);
    public sealed record CustomerEnrollmentCreate(string CustomerId);
    public sealed record Deleted(Guid Id);
}

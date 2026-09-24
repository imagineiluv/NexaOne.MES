using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Collaboration;

namespace NexaOne.Server.Gateway;

[ApiController]
[Authorize]
[Route("api/v1/collaboration/deliveries/{tenantId:guid}/{organizationId:guid}")]
public sealed class DeliveryController(IDeliveryBridge bridge, ILogger<DeliveryController> logger) : ControllerBase
{
    [HttpGet("/api/v1/collaboration/deliveries/scopes/me")]
    public Task<IActionResult> ListScopes(CancellationToken ct,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListAccessibleScopesAsync(user, offset, limit, ct));

    [HttpPost("templates")]
    public Task<IActionResult> CreateTemplate(Guid tenantId, Guid organizationId,
        [FromBody] TemplateCreate command, CancellationToken ct)
        => Execute(user => bridge.CreateTemplateAsync(user, tenantId, organizationId,
            command.Name, command.Subject, command.Body, command.Variables, ct));

    [HttpPost("templates/{id:guid}/deactivate")]
    public Task<IActionResult> DeactivateTemplate(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.DeactivateTemplateAsync(user, tenantId, organizationId, id, command.Version, ct));

    [HttpPost("profiles")]
    public Task<IActionResult> CreateProfile(Guid tenantId, Guid organizationId,
        [FromBody] ProfileCreate command, CancellationToken ct)
        => Execute(user => bridge.CreateProfileAsync(user, tenantId, organizationId, command.Name,
            command.ProviderKey, command.CredentialReference, command.ToPolicy(), ct));

    [HttpPost("profiles/{id:guid}/deactivate")]
    public Task<IActionResult> DeactivateProfile(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.DeactivateProfileAsync(user, tenantId, organizationId, id, command.Version, ct));

    [HttpPost]
    public Task<IActionResult> Queue(Guid tenantId, Guid organizationId,
        [FromBody] DeliveryCreate command, CancellationToken ct)
        => Execute(user => bridge.QueueAsync(user, tenantId, organizationId, command.OperationId,
            new(command.TemplateId, command.ProfileId, command.Recipient, command.Variables,
                command.ScheduledAt), ct));

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetAsync(user, tenantId, organizationId, id, ct));

    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.CancelAsync(user, tenantId, organizationId, id, command.Version, ct));

    [HttpGet("dead-letters")]
    public Task<IActionResult> ListDeadLetters(Guid tenantId, Guid organizationId, CancellationToken ct,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListDeadLettersAsync(user, tenantId, organizationId, offset, limit, ct));

    [HttpPost("{id:guid}/dead-letter/retry")]
    public Task<IActionResult> RetryDeadLetter(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] DeadLetterCommand command, CancellationToken ct)
        => Execute(user => bridge.RetryDeadLetterAsync(user, tenantId, organizationId,
            command.OperationId, id, command.Version, ct));

    [HttpPost("{id:guid}/dead-letter/discard")]
    public Task<IActionResult> DiscardDeadLetter(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] DeadLetterCommand command, CancellationToken ct)
        => Execute(user => bridge.DiscardDeadLetterAsync(user, tenantId, organizationId,
            command.OperationId, id, command.Version, ct));

    private async Task<IActionResult> Execute<T>(Func<string, Task<T>> action)
    {
        var userId = User.CurrentUserId();
        if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();
        try { return Ok(await action(userId)); }
        catch (BusinessException error)
        {
            if (error.Code == "BUSINESS_ACCESS_DENIED") return Forbid();
            var status = error.Code.EndsWith("_NOT_FOUND", StringComparison.Ordinal) ? 404
                : error.Code.StartsWith("INVALID_", StringComparison.Ordinal) ? 400 : 409;
            return StatusCode(status, new { code = error.Code });
        }
        catch (DBConcurrencyException) { return Conflict(new { code = "BUSINESS_VERSION_CONFLICT" }); }
        catch (Exception error) when (error is DbException
            || error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(inner => inner is DbException))
        {
            logger.LogError(error, "Delivery persistence failed; write outcome may be unknown.");
            return Problem(statusCode: 503, title: "Delivery storage is unavailable.",
                detail: "A write outcome may be unknown. Retry operation-based writes with the same operation ID; "
                    + "read current state before retrying versioned actions.");
        }
    }

    public sealed record TemplateCreate(string Name, string Subject, string Body, IReadOnlyList<string>? Variables);
    public sealed record ProfileCreate(string Name, string ProviderKey, string CredentialReference,
        int MaxAttempts, long InitialDelaySeconds, long MaximumDelaySeconds)
    {
        internal DeliveryRetryPolicy ToPolicy()
        {
            const long maximum = 7 * 24 * 60 * 60;
            if (InitialDelaySeconds is < 1 or > maximum
                || MaximumDelaySeconds < InitialDelaySeconds || MaximumDelaySeconds > maximum)
                throw new BusinessException("INVALID_BUSINESS_INPUT");
            return new(MaxAttempts, TimeSpan.FromSeconds(InitialDelaySeconds),
                TimeSpan.FromSeconds(MaximumDelaySeconds));
        }
    }
    public sealed record DeliveryCreate(Guid OperationId, Guid TemplateId, Guid ProfileId, string Recipient,
        IReadOnlyList<DeliveryVariable>? Variables = null, DateTimeOffset? ScheduledAt = null);
    public sealed record VersionedCommand(Guid Version);
    public sealed record DeadLetterCommand(Guid OperationId, Guid Version);
}

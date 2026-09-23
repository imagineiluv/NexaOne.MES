using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.Server.Gateway;

[ApiController]
[Authorize]
[Route("api/v1/erp/recurring/{tenantId:guid}/{organizationId:guid}")]
public sealed class RecurringController(IRecurringBridge bridge, ILogger<RecurringController> logger) : ControllerBase
{
    [HttpGet("/api/v1/erp/recurring/scopes/me")]
    public Task<IActionResult> ListScopes(CancellationToken ct, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Execute(user => bridge.ListAccessibleScopesAsync(user, offset, limit, ct));

    [HttpGet("rules")]
    public Task<IActionResult> ListRules(Guid tenantId, Guid organizationId,
        [FromQuery] RecurringRuleQuery query, CancellationToken ct)
        => Execute(user => bridge.ListRulesAsync(user, tenantId, organizationId, query, ct));

    [HttpPost("rules")]
    public Task<IActionResult> CreateRule(Guid tenantId, Guid organizationId,
        [FromBody] RuleCreate command, CancellationToken ct)
        => Execute(user => bridge.CreateRuleAsync(user, tenantId, organizationId,
            command.OperationId, command.ToInput(), ct));

    [HttpGet("rules/{id:guid}")]
    public Task<IActionResult> GetRule(Guid tenantId, Guid organizationId, Guid id, CancellationToken ct)
        => Execute(user => bridge.GetRuleAsync(user, tenantId, organizationId, id, ct));

    [HttpPost("rules/{id:guid}/deactivate")]
    public Task<IActionResult> DeactivateRule(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] VersionedCommand command, CancellationToken ct)
        => Execute(user => bridge.DeactivateRuleAsync(user, tenantId, organizationId,
            id, command.Version, ct));

    [HttpPost("rules/{id:guid}/occurrences")]
    public Task<IActionResult> ExecuteOccurrence(Guid tenantId, Guid organizationId, Guid id,
        [FromBody] OccurrenceCommand command, CancellationToken ct)
        => Execute(user => bridge.ExecuteOccurrenceAsync(user, tenantId, organizationId,
            id, command.Month, ct));

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
            logger.LogError(error, "Recurring ERP persistence failed; write outcome may be unknown.");
            return Problem(statusCode: 503, title: "Recurring ERP storage is unavailable.",
                detail: "A write outcome may be unknown. Read the rule before retrying creation or deactivation; "
                    + "retry occurrence execution with the same rule and month.");
        }
    }

    /// <summary>JSON-safe recurring-rule command. Exactly one template matching Target is required.</summary>
    public sealed record RuleCreate(Guid OperationId, string Name, RecurringSchedule Schedule,
        RecurringTarget Target, RecurringBillingTemplate? Billing = null,
        RecurringIncomeTemplate? Income = null, RecurringExpenseTemplate? Expense = null)
    {
        internal RecurringRuleInput ToInput()
        {
            RecurringTemplate template = (Target, Billing, Income, Expense) switch
            {
                (RecurringTarget.Billing, { } value, null, null) => value,
                (RecurringTarget.Income, null, { } value, null) => value,
                (RecurringTarget.Expense, null, null, { } value) => value,
                _ => throw new BusinessException("INVALID_BUSINESS_INPUT")
            };
            return new(Name, Schedule, template);
        }
    }
    public sealed record VersionedCommand(Guid Version);
    public sealed record OccurrenceCommand(DateOnly Month);
}

using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ServiceContracts.Erp;

/// <summary>Organization-scoped monthly ERP rules and their durable execution occurrences.</summary>
/// <remarks>Every call rechecks current SYS membership and an explicit recurring grant. A host scheduler may call
/// ExecuteOccurrenceAsync repeatedly for the same month; the persisted rule/month identity returns the same result.</remarks>
public interface IRecurringBridge : INexaModuleBridge
{
    Task<BusinessPage<BusinessMembership>> ListAccessibleScopesAsync(string userId,
        int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<RecurringRule> CreateRuleAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, RecurringRuleInput input, CancellationToken ct = default);
    Task<RecurringRule> GetRuleAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<BusinessPage<RecurringRule>> ListRulesAsync(string userId, Guid tenantId, Guid organizationId,
        RecurringRuleQuery? query = null, CancellationToken ct = default);
    Task<BusinessPage<RecurringOccurrenceHistoryItem>> ListOccurrencesAsync(
        string userId, Guid tenantId, Guid organizationId,
        RecurringOccurrenceQuery? query = null, CancellationToken ct = default);
    Task<RecurringRule> DeactivateRuleAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
    Task<RecurringExecution> ExecuteOccurrenceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid ruleId, DateOnly month, CancellationToken ct = default);
}

/// <summary>Scoped filters for durable recurring occurrence history, newest month first.</summary>
public sealed record RecurringOccurrenceQuery(Guid? RuleId = null, RecurringTarget? Target = null,
    DateOnly? StartMonth = null, DateOnly? EndMonth = null, int Offset = 0, int Limit = 50);

/// <summary>A durable occurrence paired with the current immutable rule name.</summary>
public sealed record RecurringOccurrenceHistoryItem(RecurringOccurrence Occurrence, string RuleName);

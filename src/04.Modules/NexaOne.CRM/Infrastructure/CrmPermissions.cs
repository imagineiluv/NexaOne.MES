namespace NexaOne.CRM.Infrastructure;

internal static class CrmPermissions
{
    public const string Read = "crm.read";
    public const string ManagePipelines = "crm.pipeline.manage";
    public const string ManageDeals = "crm.deal.manage";
    public const string DeleteDeals = "crm.deal.delete";
    public const string AllDeals = "crm.deal.all";
    public const string AssignedDeals = "crm.deal.assigned";
    public const string CreatedDeals = "crm.deal.created";
    public const string AssignedOrCreatedDeals = "crm.deal.assigned-or-created";
}

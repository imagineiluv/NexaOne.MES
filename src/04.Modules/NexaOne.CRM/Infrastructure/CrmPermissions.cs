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
    public const string ReadProjects = "crm.project.read";
    public const string ManageProjects = "crm.project.manage";
    public const string DeleteProjects = "crm.project.delete";
    public const string AllProjects = "crm.project.all";
    public const string AssignedProjects = "crm.project.assigned";
    public const string CreatedProjects = "crm.project.created";
    public const string AssignedOrCreatedProjects = "crm.project.assigned-or-created";
    public const string ReadTeams = "crm.team.read";
    public const string ManageTeams = "crm.team.manage";
    public const string DeleteTeams = "crm.team.delete";
    public const string LinkCustomer = "crm.project.link-customer";
}

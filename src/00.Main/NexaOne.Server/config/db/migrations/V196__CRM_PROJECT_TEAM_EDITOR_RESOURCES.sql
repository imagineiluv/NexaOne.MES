INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'crm.billable' AS RESOURCE_KEY, 'Billable' AS VALUE
    UNION ALL SELECT 'crm.budget', 'Budget'
    UNION ALL SELECT 'crm.budgetCost', 'Cost'
    UNION ALL SELECT 'crm.budgetHours', 'Hours'
    UNION ALL SELECT 'crm.budgetType', 'Budget type'
    UNION ALL SELECT 'crm.code', 'Code'
    UNION ALL SELECT 'crm.customerLinkPermissionRequired', 'Customer-link permission is required to change relationships for a project with a customer.'
    UNION ALL SELECT 'crm.endDate', 'End date'
    UNION ALL SELECT 'crm.managerIds', 'Manager IDs'
    UNION ALL SELECT 'crm.memberIds', 'Member IDs'
    UNION ALL SELECT 'crm.newProject', 'New project'
    UNION ALL SELECT 'crm.newTeam', 'New team'
    UNION ALL SELECT 'crm.prefix', 'Prefix'
    UNION ALL SELECT 'crm.projectDeleted', 'Project deleted.'
    UNION ALL SELECT 'crm.projectEditor', 'Project editor'
    UNION ALL SELECT 'crm.projectInputRequired', 'Check the project name, dates and budget.'
    UNION ALL SELECT 'crm.projectLinksSaved', 'Project relationships saved.'
    UNION ALL SELECT 'crm.projectRelations', 'Project relationships'
    UNION ALL SELECT 'crm.projectSaved', 'Project saved.'
    UNION ALL SELECT 'crm.public', 'Public'
    UNION ALL SELECT 'crm.relationIdsInvalid', 'Relationship IDs must be valid comma-separated GUIDs.'
    UNION ALL SELECT 'crm.requirePlan', 'Require plan to track'
    UNION ALL SELECT 'crm.saveRelations', 'Save relationships'
    UNION ALL SELECT 'crm.shareProfile', 'Share profile'
    UNION ALL SELECT 'crm.startDate', 'Start date'
    UNION ALL SELECT 'crm.teamDeleted', 'Team deleted.'
    UNION ALL SELECT 'crm.teamEditor', 'Team editor'
    UNION ALL SELECT 'crm.teamIds', 'Team IDs'
    UNION ALL SELECT 'crm.teamSaved', 'Team saved.'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

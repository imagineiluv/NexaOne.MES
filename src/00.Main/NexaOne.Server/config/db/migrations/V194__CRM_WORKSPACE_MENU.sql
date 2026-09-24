-- CRM workspace under sales. No role or business grants are seeded here.
-- SQLITE-OMIT-BEGIN
INSERT INTO SYS_MENU
    (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, UI_ID, VALID_STATE)
SELECT 'NX_CRM_WORKSPACE', N'CRM 거래·프로젝트', 'FACTORY_SLS',
       (SELECT COALESCE(MAX(DISPLAY_SEQUENCE), 0) + CASE WHEN MAX(DISPLAY_SEQUENCE) = 2147483647 THEN 0 ELSE 1 END
          FROM SYS_MENU WHERE PARENT_MENU_ID = 'FACTORY_SLS'),
       'Screen', '', 'NX_CRM_WORKSPACE', 'Valid'
WHERE EXISTS (SELECT 1 FROM SYS_MENU WHERE MENU_ID = 'FACTORY_SLS' AND MENU_TYPE = 'Folder')
  AND NOT EXISTS (SELECT 1 FROM SYS_MENU
                   WHERE UPPER(MENU_ID) = 'NX_CRM_WORKSPACE' OR UPPER(UI_ID) = 'NX_CRM_WORKSPACE');
-- SQLITE-OMIT-END

INSERT INTO SYS_MENU
    (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, UI_ID, VALID_STATE)
SELECT 'NX_CRM_WORKSPACE', 'CRM 거래·프로젝트', 'FACTORY_SLS',
       (SELECT COALESCE(MAX(DISPLAY_SEQUENCE), 0) + CASE WHEN MAX(DISPLAY_SEQUENCE) = 2147483647 THEN 0 ELSE 1 END
          FROM SYS_MENU WHERE PARENT_MENU_ID = 'FACTORY_SLS'),
       'Screen', '', 'NX_CRM_WORKSPACE', 'Valid'
WHERE EXISTS (SELECT 1 FROM SYS_MENU WHERE MENU_ID = 'FACTORY_SLS' AND MENU_TYPE = 'Folder')
  AND NOT EXISTS (SELECT 1 FROM SYS_MENU
                   WHERE UPPER(MENU_ID) = 'NX_CRM_WORKSPACE' OR UPPER(UI_ID) = 'NX_CRM_WORKSPACE');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'menu.NX_CRM_WORKSPACE' AS RESOURCE_KEY, 'CRM deals & projects' AS VALUE
    UNION ALL SELECT 'crm.assignees', 'Assignees'
    UNION ALL SELECT 'crm.client', 'Client'
    UNION ALL SELECT 'crm.conflict', 'The operation conflicts with the current state. Refresh the list.'
    UNION ALL SELECT 'crm.contact', 'CRM contact'
    UNION ALL SELECT 'crm.createdBy', 'Created by'
    UNION ALL SELECT 'crm.customerEnrollments', 'CRM customers'
    UNION ALL SELECT 'crm.deal', 'Deal'
    UNION ALL SELECT 'crm.deals', 'Deals'
    UNION ALL SELECT 'crm.details', 'Selected record'
    UNION ALL SELECT 'crm.empty', 'No results on this page.'
    UNION ALL SELECT 'crm.enroll', 'Enroll customer'
    UNION ALL SELECT 'crm.enrollCustomer', 'MDM customer code'
    UNION ALL SELECT 'crm.enrolledBy', 'Enrolled by'
    UNION ALL SELECT 'crm.intro', 'Review pipelines, visible deals and projects, and manage CRM customer links in an accessible organization.'
    UNION ALL SELECT 'crm.members', 'Members'
    UNION ALL SELECT 'crm.noReadAccess', 'This scope is accessible, but you currently have no CRM read permission.'
    UNION ALL SELECT 'crm.noScopes', 'No CRM scopes are currently accessible.'
    UNION ALL SELECT 'crm.notFound', 'The record was not found. Refresh the list.'
    UNION ALL SELECT 'crm.pipeline', 'Pipeline'
    UNION ALL SELECT 'crm.pipelines', 'Pipelines'
    UNION ALL SELECT 'crm.probability', 'Probability'
    UNION ALL SELECT 'crm.projectCancelled', 'Cancelled'
    UNION ALL SELECT 'crm.projectCompleted', 'Completed'
    UNION ALL SELECT 'crm.projectInProgress', 'In progress'
    UNION ALL SELECT 'crm.projectOpen', 'Open'
    UNION ALL SELECT 'crm.project', 'Project'
    UNION ALL SELECT 'crm.projects', 'Projects'
    UNION ALL SELECT 'crm.refresh', 'Refresh CRM data'
    UNION ALL SELECT 'crm.scope', 'My CRM scopes'
    UNION ALL SELECT 'crm.select', 'Select'
    UNION ALL SELECT 'crm.selectedScope', 'CRM scope'
    UNION ALL SELECT 'crm.stage', 'Stage'
    UNION ALL SELECT 'crm.stages', 'Stages'
    UNION ALL SELECT 'crm.status', 'Status'
    UNION ALL SELECT 'crm.title', 'CRM workspace'
    UNION ALL SELECT 'crm.team', 'Team'
    UNION ALL SELECT 'crm.teams', 'Teams'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

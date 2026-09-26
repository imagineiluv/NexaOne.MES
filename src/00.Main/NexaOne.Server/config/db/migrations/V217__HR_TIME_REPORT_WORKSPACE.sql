-- HR time report under the existing worker folder. No role or business grants are seeded here.
-- SQLITE-OMIT-BEGIN
INSERT INTO SYS_MENU
    (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, UI_ID, VALID_STATE)
SELECT 'NX_HR_TIME_REPORT', N'시간 보고', 'FACTORY_STD_SINGLE_WORKER',
       (SELECT COALESCE(MAX(DISPLAY_SEQUENCE), 0) + CASE WHEN MAX(DISPLAY_SEQUENCE) = 2147483647 THEN 0 ELSE 1 END
          FROM SYS_MENU WHERE PARENT_MENU_ID = 'FACTORY_STD_SINGLE_WORKER'),
       'Screen', '', 'NX_HR_TIME_REPORT', 'Valid'
WHERE EXISTS (SELECT 1 FROM SYS_MENU WHERE MENU_ID = 'FACTORY_STD_SINGLE_WORKER' AND MENU_TYPE = 'Folder')
  AND NOT EXISTS (SELECT 1 FROM SYS_MENU
                   WHERE UPPER(MENU_ID) = 'NX_HR_TIME_REPORT' OR UPPER(UI_ID) = 'NX_HR_TIME_REPORT');
-- SQLITE-OMIT-END

INSERT INTO SYS_MENU
    (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, UI_ID, VALID_STATE)
SELECT 'NX_HR_TIME_REPORT', '시간 보고', 'FACTORY_STD_SINGLE_WORKER',
       (SELECT COALESCE(MAX(DISPLAY_SEQUENCE), 0) + CASE WHEN MAX(DISPLAY_SEQUENCE) = 2147483647 THEN 0 ELSE 1 END
          FROM SYS_MENU WHERE PARENT_MENU_ID = 'FACTORY_STD_SINGLE_WORKER'),
       'Screen', '', 'NX_HR_TIME_REPORT', 'Valid'
WHERE EXISTS (SELECT 1 FROM SYS_MENU WHERE MENU_ID = 'FACTORY_STD_SINGLE_WORKER' AND MENU_TYPE = 'Folder')
  AND NOT EXISTS (SELECT 1 FROM SYS_MENU
                   WHERE UPPER(MENU_ID) = 'NX_HR_TIME_REPORT' OR UPPER(UI_ID) = 'NX_HR_TIME_REPORT');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'menu.NX_HR_TIME_REPORT' AS RESOURCE_KEY, 'Time report' AS VALUE
    UNION ALL SELECT 'hrReport.all', 'All'
    UNION ALL SELECT 'hrReport.approval', 'Approval state'
    UNION ALL SELECT 'hrReport.approved', 'Approved'
    UNION ALL SELECT 'hrReport.description', 'Description'
    UNION ALL SELECT 'hrReport.downloadStarted', 'CSV download started.'
    UNION ALL SELECT 'hrReport.duration', 'Hours'
    UNION ALL SELECT 'hrReport.employee', 'Employee ID'
    UNION ALL SELECT 'hrReport.empty', 'No completed time entries match these filters.'
    UNION ALL SELECT 'hrReport.end', 'End date'
    UNION ALL SELECT 'hrReport.export', 'Export loaded CSV'
    UNION ALL SELECT 'hrReport.failed', 'The time report could not be loaded. Check the filters and try again.'
    UNION ALL SELECT 'hrReport.filters', 'Report filters'
    UNION ALL SELECT 'hrReport.forbidden', 'Your time-read access changed. Refresh organizations.'
    UNION ALL SELECT 'hrReport.generated', 'Generated UTC'
    UNION ALL SELECT 'hrReport.intro', 'Review completed time entries by period and approval state, then export a validated CSV.'
    UNION ALL SELECT 'hrReport.invalidId', 'Enter a valid GUID in each populated ID filter.'
    UNION ALL SELECT 'hrReport.invalidPeriod', 'End date cannot precede start date, and the report period is limited to 366 days.'
    UNION ALL SELECT 'hrReport.loading', 'Checking time-report access…'
    UNION ALL SELECT 'hrReport.next', 'Next'
    UNION ALL SELECT 'hrReport.noScopes', 'No organizations are available for time reporting.'
    UNION ALL SELECT 'hrReport.organization', 'Organization'
    UNION ALL SELECT 'hrReport.pages', 'Time report pages'
    UNION ALL SELECT 'hrReport.period', 'Recorded time UTC'
    UNION ALL SELECT 'hrReport.previous', 'Previous'
    UNION ALL SELECT 'hrReport.project', 'Project ID'
    UNION ALL SELECT 'hrReport.references', 'Work references'
    UNION ALL SELECT 'hrReport.refreshScopes', 'Refresh organizations'
    UNION ALL SELECT 'hrReport.rejected', 'Rejected'
    UNION ALL SELECT 'hrReport.results', 'Time entries'
    UNION ALL SELECT 'hrReport.rows', 'Entries'
    UNION ALL SELECT 'hrReport.run', 'Load time entries'
    UNION ALL SELECT 'hrReport.scopeHelp', 'Only organizations where you currently hold hr.time.read are shown.'
    UNION ALL SELECT 'hrReport.scopes', 'Report organization'
    UNION ALL SELECT 'hrReport.signIn', 'Your session expired. Sign in again.'
    UNION ALL SELECT 'hrReport.start', 'Start date'
    UNION ALL SELECT 'hrReport.submitted', 'Submitted'
    UNION ALL SELECT 'hrReport.task', 'Task ID'
    UNION ALL SELECT 'hrReport.tenant', 'Tenant'
    UNION ALL SELECT 'hrReport.title', 'Time report'
    UNION ALL SELECT 'hrReport.tooLarge', 'The result exceeds 10,000 rows. Narrow the period or ID filters and try again.'
    UNION ALL SELECT 'hrReport.totalHours', 'Total hours'
    UNION ALL SELECT 'hrReport.unsubmitted', 'Unsubmitted'
    UNION ALL SELECT 'hrReport.working', 'Processing request…'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

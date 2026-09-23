-- Recurring ERP workspace menu and English resources. Missing rows only; preserve customized values.
-- No role mappings or grants: navigation remains separate from scoped business authorization.
-- SQLITE-OMIT-BEGIN
INSERT INTO SYS_MENU (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, UI_ID, VALID_STATE)
SELECT 'NX_RECURRING_WORKSPACE', N'반복 ERP 규칙', 'FACTORY_SLS',
       (SELECT COALESCE(MAX(DISPLAY_SEQUENCE), 0) + CASE WHEN MAX(DISPLAY_SEQUENCE) = 2147483647 THEN 0 ELSE 1 END FROM SYS_MENU WHERE PARENT_MENU_ID = 'FACTORY_SLS'),
       'Screen', '', 'NX_RECURRING_WORKSPACE', 'Valid'
WHERE EXISTS (SELECT 1 FROM SYS_MENU WHERE MENU_ID = 'FACTORY_SLS' AND MENU_TYPE = 'Folder')
  AND NOT EXISTS (SELECT 1 FROM SYS_MENU WHERE UPPER(MENU_ID) = 'NX_RECURRING_WORKSPACE' OR UPPER(UI_ID) = 'NX_RECURRING_WORKSPACE');
-- SQLITE-OMIT-END

INSERT INTO SYS_MENU (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, UI_ID, VALID_STATE)
SELECT 'NX_RECURRING_WORKSPACE', '반복 ERP 규칙', 'FACTORY_SLS',
       (SELECT COALESCE(MAX(DISPLAY_SEQUENCE), 0) + CASE WHEN MAX(DISPLAY_SEQUENCE) = 2147483647 THEN 0 ELSE 1 END FROM SYS_MENU WHERE PARENT_MENU_ID = 'FACTORY_SLS'),
       'Screen', '', 'NX_RECURRING_WORKSPACE', 'Valid'
WHERE EXISTS (SELECT 1 FROM SYS_MENU WHERE MENU_ID = 'FACTORY_SLS' AND MENU_TYPE = 'Folder')
  AND NOT EXISTS (SELECT 1 FROM SYS_MENU WHERE UPPER(MENU_ID) = 'NX_RECURRING_WORKSPACE' OR UPPER(UI_ID) = 'NX_RECURRING_WORKSPACE');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'menu.NX_RECURRING_WORKSPACE' AS RESOURCE_KEY, 'Recurring ERP rules' AS VALUE
    UNION ALL SELECT 'recurring.acknowledge', 'I verified the outcome and want to close this recovery record.'
    UNION ALL SELECT 'recurring.actionNote', 'Rules are immutable after creation. Deactivate an old rule and create a replacement to change it.'
    UNION ALL SELECT 'recurring.actions', 'Rule actions'
    UNION ALL SELECT 'recurring.allTargets', 'All targets'
    UNION ALL SELECT 'recurring.billing', 'Billing'
    UNION ALL SELECT 'recurring.bonus', 'Bonus'
    UNION ALL SELECT 'recurring.cancel', 'Cancel'
    UNION ALL SELECT 'recurring.categoryId', 'Category ID'
    UNION ALL SELECT 'recurring.chooseRule', 'Select rule'
    UNION ALL SELECT 'recurring.chooseScope', 'Select a scope to see its rules and permitted actions.'
    UNION ALL SELECT 'recurring.clearRecovery', 'Close recovery record'
    UNION ALL SELECT 'recurring.confirmed', 'Confirmed request'
    UNION ALL SELECT 'recurring.conflict', 'The request conflicts with the current rule state. Refresh the list.'
    UNION ALL SELECT 'recurring.contactId', 'Contact ID'
    UNION ALL SELECT 'recurring.create', 'Create rule'
    UNION ALL SELECT 'recurring.created', 'The recurring rule was created.'
    UNION ALL SELECT 'recurring.day', 'Day'
    UNION ALL SELECT 'recurring.deactivate', 'Deactivate rule'
    UNION ALL SELECT 'recurring.deactivated', 'The recurring rule was deactivated.'
    UNION ALL SELECT 'recurring.dueDays', 'Days until due'
    UNION ALL SELECT 'recurring.employeeId', 'Employee ID (optional)'
    UNION ALL SELECT 'recurring.endMonth', 'End month (optional)'
    UNION ALL SELECT 'recurring.execute', 'Run monthly occurrence'
    UNION ALL SELECT 'recurring.executed', 'The monthly occurrence was run.'
    UNION ALL SELECT 'recurring.expense', 'Expense'
    UNION ALL SELECT 'recurring.expenseType', 'Expense type'
    UNION ALL SELECT 'recurring.foreignScope', 'A rule from another scope cannot be selected.'
    UNION ALL SELECT 'recurring.income', 'Income'
    UNION ALL SELECT 'recurring.intro', 'Create recurring billing, income and expense rules by organization, then run monthly occurrences on demand.'
    UNION ALL SELECT 'recurring.invalidMonth', 'Select a month to run.'
    UNION ALL SELECT 'recurring.invalidRule', 'Check the rule name, month range, day, currency, amounts and required IDs.'
    UNION ALL SELECT 'recurring.latestResult', 'Latest execution result in this tab'
    UNION ALL SELECT 'recurring.month', 'Occurrence month'
    UNION ALL SELECT 'recurring.newRule', 'New rule'
    UNION ALL SELECT 'recurring.noHistory', 'The server has no occurrence-history list API, so this result is shown only in the current tab.'
    UNION ALL SELECT 'recurring.noReadAccess', 'This scope has no rule-list permission (recurring.read). Permitted write and execute actions remain available.'
    UNION ALL SELECT 'recurring.noScopes', 'No recurring-rule scopes are currently accessible.'
    UNION ALL SELECT 'recurring.notFound', 'The rule or referenced master data was not found.'
    UNION ALL SELECT 'recurring.period', 'Period'
    UNION ALL SELECT 'recurring.purpose', 'Purpose'
    UNION ALL SELECT 'recurring.receipt', 'Receipt locator'
    UNION ALL SELECT 'recurring.recoveryError', 'The tab recovery store is unavailable. No new request was started.'
    UNION ALL SELECT 'recurring.referenceNote', 'IDs must refer to active records in this organization. Project and tag fields are omitted because this product does not yet provide their master-data workflow.'
    UNION ALL SELECT 'recurring.resourceId', 'Created resource ID'
    UNION ALL SELECT 'recurring.resultId', 'Result ID'
    UNION ALL SELECT 'recurring.retryNote', 'Retry sends the stored request with the same identity. Server replay protection prevents duplicate creation.'
    UNION ALL SELECT 'recurring.retryRecovery', 'Retry recovery storage'
    UNION ALL SELECT 'recurring.retryWrite', 'Retry same request'
    UNION ALL SELECT 'recurring.rules', 'Recurring rules'
    UNION ALL SELECT 'recurring.savedRecovery', 'The action completed, but its recovery record could not be updated. Verify the displayed result, then close the record.'
    UNION ALL SELECT 'recurring.scopes', 'My recurring-rule scopes'
    UNION ALL SELECT 'recurring.split', 'Split across active employees'
    UNION ALL SELECT 'recurring.startMonth', 'Start month'
    UNION ALL SELECT 'recurring.target', 'Target'
    UNION ALL SELECT 'recurring.title', 'Recurring ERP rules'
    UNION ALL SELECT 'recurring.unknown', 'Request with unknown outcome'
    UNION ALL SELECT 'recurring.unknownOutcome', 'The outcome could not be confirmed. Retry the same request safely.'
    UNION ALL SELECT 'recurring.vendorId', 'Vendor ID'
    UNION ALL SELECT 'recurring.writeFailed', 'The action could not be completed.'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

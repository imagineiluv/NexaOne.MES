-- Billing workspace navigation under the SmartUX sales folder (FACTORY_SLS); Korean labels are UI fallbacks.
-- Only extend an existing tree. An empty menu must remain empty until full JSON/ops seeding.
-- Append without moving existing siblings; preserve customized menus, route aliases and resources.
-- No role mappings or grants: menu discovery is separate from current scoped business authorization.
-- At INT_MAX, share the last sequence (the menu query then orders by ID) rather than overflow or renumber.
-- SQL Server needs Unicode literals regardless of database code page. SQLite strips this block;
-- the portable inserts below then supply the same values without the N literal prefix.
-- SQLITE-OMIT-BEGIN
INSERT INTO SYS_MENU
    (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, UI_ID, VALID_STATE)
SELECT 'NX_BILLING_WORKSPACE', N'견적·청구·입금', 'FACTORY_SLS',
       (SELECT COALESCE(MAX(DISPLAY_SEQUENCE), 0) + CASE WHEN MAX(DISPLAY_SEQUENCE) = 2147483647 THEN 0 ELSE 1 END
          FROM SYS_MENU WHERE PARENT_MENU_ID = 'FACTORY_SLS'),
       'Screen', '', 'NX_BILLING_WORKSPACE', 'Valid'
WHERE EXISTS (SELECT 1 FROM SYS_MENU WHERE MENU_ID = 'FACTORY_SLS' AND MENU_TYPE = 'Folder')
  AND NOT EXISTS (SELECT 1 FROM SYS_MENU
                   WHERE UPPER(MENU_ID) = 'NX_BILLING_WORKSPACE' OR UPPER(UI_ID) = 'NX_BILLING_WORKSPACE');
-- SQLITE-OMIT-END

INSERT INTO SYS_MENU
    (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, UI_ID, VALID_STATE)
SELECT 'NX_BILLING_WORKSPACE', '견적·청구·입금', 'FACTORY_SLS',
       (SELECT COALESCE(MAX(DISPLAY_SEQUENCE), 0) + CASE WHEN MAX(DISPLAY_SEQUENCE) = 2147483647 THEN 0 ELSE 1 END
          FROM SYS_MENU WHERE PARENT_MENU_ID = 'FACTORY_SLS'),
       'Screen', '', 'NX_BILLING_WORKSPACE', 'Valid'
WHERE EXISTS (SELECT 1 FROM SYS_MENU WHERE MENU_ID = 'FACTORY_SLS' AND MENU_TYPE = 'Folder')
  AND NOT EXISTS (SELECT 1 FROM SYS_MENU
                   WHERE UPPER(MENU_ID) = 'NX_BILLING_WORKSPACE' OR UPPER(UI_ID) = 'NX_BILLING_WORKSPACE');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'menu.NX_BILLING_WORKSPACE', 'COMMON', 'EnUs', 'Estimates, invoices & payments'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE
                   WHERE RESOURCE_KEY = 'menu.NX_BILLING_WORKSPACE' AND LANGUAGE = 'EnUs');

-- Inventory workspace navigation and English resources (Korean labels are UI fallbacks).
-- Only extend an existing IVT tree. An empty menu must remain empty until full JSON/ops seeding.
-- Append without moving existing siblings; preserve customized menus, route aliases and resources.
-- No role mappings or grants: menu discovery is separate from current scoped business authorization.
-- At INT_MAX, share the last sequence (the menu query then orders by ID) rather than overflow or renumber.
-- SQL Server needs Unicode literals regardless of database code page. SQLite strips this block;
-- the portable inserts below then supply the same values without the N literal prefix.
-- SQLITE-OMIT-BEGIN
INSERT INTO SYS_MENU
    (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, UI_ID, VALID_STATE)
SELECT 'NX_INVENTORY_WORKSPACE', N'재고·장비 공유', 'FACTORY_IVT',
       (SELECT COALESCE(MAX(DISPLAY_SEQUENCE), 0) + CASE WHEN MAX(DISPLAY_SEQUENCE) = 2147483647 THEN 0 ELSE 1 END
          FROM SYS_MENU WHERE PARENT_MENU_ID = 'FACTORY_IVT'),
       'Screen', '', 'NX_INVENTORY_WORKSPACE', 'Valid'
WHERE EXISTS (SELECT 1 FROM SYS_MENU WHERE MENU_ID = 'FACTORY_IVT' AND MENU_TYPE = 'Folder')
  AND NOT EXISTS (SELECT 1 FROM SYS_MENU
                   WHERE UPPER(MENU_ID) = 'NX_INVENTORY_WORKSPACE' OR UPPER(UI_ID) = 'NX_INVENTORY_WORKSPACE');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT 'inventory.loading', 'COMMON', 'EnUs', N'Loading…'
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE
                   WHERE RESOURCE_KEY = 'inventory.loading' AND LANGUAGE = 'EnUs');
-- SQLITE-OMIT-END

INSERT INTO SYS_MENU
    (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, UI_ID, VALID_STATE)
SELECT 'NX_INVENTORY_WORKSPACE', '재고·장비 공유', 'FACTORY_IVT',
       (SELECT COALESCE(MAX(DISPLAY_SEQUENCE), 0) + CASE WHEN MAX(DISPLAY_SEQUENCE) = 2147483647 THEN 0 ELSE 1 END
          FROM SYS_MENU WHERE PARENT_MENU_ID = 'FACTORY_IVT'),
       'Screen', '', 'NX_INVENTORY_WORKSPACE', 'Valid'
WHERE EXISTS (SELECT 1 FROM SYS_MENU WHERE MENU_ID = 'FACTORY_IVT' AND MENU_TYPE = 'Folder')
  AND NOT EXISTS (SELECT 1 FROM SYS_MENU
                   WHERE UPPER(MENU_ID) = 'NX_INVENTORY_WORKSPACE' OR UPPER(UI_ID) = 'NX_INVENTORY_WORKSPACE');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE
FROM (
    SELECT 'menu.NX_INVENTORY_WORKSPACE' AS RESOURCE_KEY, 'Inventory & equipment' AS VALUE
    UNION ALL SELECT 'error.inventoryPath', 'The inventory read path is invalid.'
    UNION ALL SELECT 'error.inventoryResponse', 'The inventory response could not be read. Please retry.'
    UNION ALL SELECT 'inventory.active', 'Active'
    UNION ALL SELECT 'inventory.activity', 'Activity'
    UNION ALL SELECT 'inventory.allStates', 'All states'
    UNION ALL SELECT 'inventory.approval', 'Approval required'
    UNION ALL SELECT 'inventory.approved', 'Approved'
    UNION ALL SELECT 'inventory.assets', 'Shared equipment'
    UNION ALL SELECT 'inventory.bookingId', 'Booking ID'
    UNION ALL SELECT 'inventory.bookingNote', 'Booking equipment, employees and requesters are shown by their stored identifiers.'
    UNION ALL SELECT 'inventory.bookings', 'Booking history'
    UNION ALL SELECT 'inventory.cancelled', 'Cancelled'
    UNION ALL SELECT 'inventory.capacity', 'Capacity'
    UNION ALL SELECT 'inventory.checkedOut', 'Checked out'
    UNION ALL SELECT 'inventory.chooseScope', 'Select a scope to see the lists you currently have permission to read.'
    UNION ALL SELECT 'inventory.code', 'Code'
    UNION ALL SELECT 'inventory.denied', 'Denied'
    UNION ALL SELECT 'inventory.employeeId', 'Employee ID'
    UNION ALL SELECT 'inventory.empty', 'No results on this page.'
    UNION ALL SELECT 'inventory.end', 'End (UTC)'
    UNION ALL SELECT 'inventory.equipmentId', 'Equipment ID'
    UNION ALL SELECT 'inventory.forbidden', 'You no longer have access to this read. Refresh scopes to check access.'
    UNION ALL SELECT 'inventory.inactive', 'Inactive'
    UNION ALL SELECT 'inventory.includeInactive', 'Include inactive'
    UNION ALL SELECT 'inventory.intro', 'Choose an accessible scope to browse enrolled products, warehouses, shared equipment and bookings.'
    UNION ALL SELECT 'inventory.invalidQuery', 'Check the search criteria and retry.'
    UNION ALL SELECT 'inventory.loading', 'Loading…'
    UNION ALL SELECT 'inventory.name', 'Name'
    UNION ALL SELECT 'inventory.next', 'Next page'
    UNION ALL SELECT 'inventory.no', 'No'
    UNION ALL SELECT 'inventory.noReadAccess', 'This scope is accessible, but you currently have no list-reading or worker-selection permission.'
    UNION ALL SELECT 'inventory.noScopes', 'No inventory scopes are currently accessible.'
    UNION ALL SELECT 'inventory.organization', 'Organization'
    UNION ALL SELECT 'inventory.page', 'Page'
    UNION ALL SELECT 'inventory.previous', 'Previous page'
    UNION ALL SELECT 'inventory.productNote', 'Enrolled products show master activity, not stock quantity or unit eligibility.'
    UNION ALL SELECT 'inventory.products', 'Enrolled products'
    UNION ALL SELECT 'inventory.quantity', 'Quantity'
    UNION ALL SELECT 'inventory.readFailed', 'The list could not be loaded. Retry the read.'
    UNION ALL SELECT 'inventory.refreshScopes', 'Refresh scopes'
    UNION ALL SELECT 'inventory.requested', 'Requested'
    UNION ALL SELECT 'inventory.requesterId', 'Requester ID'
    UNION ALL SELECT 'inventory.retry', 'Retry read'
    UNION ALL SELECT 'inventory.returned', 'Returned'
    UNION ALL SELECT 'inventory.scopes', 'My inventory scopes'
    UNION ALL SELECT 'inventory.search', 'Search'
    UNION ALL SELECT 'inventory.searchText', 'Search code or name'
    UNION ALL SELECT 'inventory.selectedWorker', 'Selected for browsing'
    UNION ALL SELECT 'inventory.selection', 'Selection'
    UNION ALL SELECT 'inventory.selectWorker', 'Select for browsing'
    UNION ALL SELECT 'inventory.signIn', 'Check your session and sign in again.'
    UNION ALL SELECT 'inventory.start', 'Start (UTC)'
    UNION ALL SELECT 'inventory.state', 'State'
    UNION ALL SELECT 'inventory.tenant', 'Tenant'
    UNION ALL SELECT 'inventory.title', 'Inventory workspace'
    UNION ALL SELECT 'inventory.total', 'Total'
    UNION ALL SELECT 'inventory.unauthorized', 'Your session expired. Sign in again.'
    UNION ALL SELECT 'inventory.unavailable', 'The service is unavailable. Retry the read shortly.'
    UNION ALL SELECT 'inventory.warehouses', 'Warehouses'
    UNION ALL SELECT 'inventory.workerId', 'Worker code'
    UNION ALL SELECT 'inventory.workerNote', 'Active workers in the bound plant. Selecting a worker here does not create or save a booking.'
    UNION ALL SELECT 'inventory.workers', 'Worker lookup and selection'
    UNION ALL SELECT 'inventory.yes', 'Yes'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

-- Expense ledger filter and paging UI resources. No role or business grants are seeded here.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'expenseWorkspace.ledgerFilters' AS RESOURCE_KEY, 'Ledger filters' AS VALUE
    UNION ALL SELECT 'expenseWorkspace.ledgerDateHelp', 'Enter both dates; a range can cover up to 366 days.'
    UNION ALL SELECT 'expenseWorkspace.fromDate', 'From date'
    UNION ALL SELECT 'expenseWorkspace.toDate', 'To date'
    UNION ALL SELECT 'expenseWorkspace.allCategories', 'All categories'
    UNION ALL SELECT 'expenseWorkspace.allVendors', 'All vendors'
    UNION ALL SELECT 'expenseWorkspace.allTypes', 'All types'
    UNION ALL SELECT 'expenseWorkspace.allStatuses', 'All statuses'
    UNION ALL SELECT 'expenseWorkspace.recordState', 'Record state'
    UNION ALL SELECT 'expenseWorkspace.clearFilters', 'Clear filters'
    UNION ALL SELECT 'expenseWorkspace.invalidLedgerRange', 'Enter both dates and choose a valid range of no more than 366 days.'
    UNION ALL SELECT 'expenseWorkspace.invalidLedgerFilter', 'Check the expense-ledger filters.'
    UNION ALL SELECT 'expenseWorkspace.noMatchingExpenses', 'No expenses match these filters.'
    UNION ALL SELECT 'expenseWorkspace.ledgerPage', '{0}–{1} of {2}'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

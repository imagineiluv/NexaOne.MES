-- Expense advanced-input and scoped master-selector resources. No role or business grants are seeded here.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'expenseWorkspace.employee' AS RESOURCE_KEY, 'Employee (optional)' AS VALUE
    UNION ALL SELECT 'expenseWorkspace.noEmployee', 'No employee'
    UNION ALL SELECT 'expenseWorkspace.employeePages', 'Employee choice pages'
    UNION ALL SELECT 'expenseWorkspace.choicePage', '{0}–{1} of {2}'
    UNION ALL SELECT 'expenseWorkspace.splitEmployees', 'Split equally across all active employees'
    UNION ALL SELECT 'expenseWorkspace.employeeOrSplit', 'Choose either one employee or a split across active employees.'
    UNION ALL SELECT 'expenseWorkspace.contact', 'Billing contact'
    UNION ALL SELECT 'expenseWorkspace.chooseContact', 'Choose billing contact'
    UNION ALL SELECT 'expenseWorkspace.contactPages', 'Billing contact choice pages'
    UNION ALL SELECT 'expenseWorkspace.billingReadForContact', 'The billing.read permission is required to choose a billing contact.'
    UNION ALL SELECT 'expenseWorkspace.project', 'Project (optional)'
    UNION ALL SELECT 'expenseWorkspace.noProject', 'No project'
    UNION ALL SELECT 'expenseWorkspace.projectPages', 'Project choice pages'
    UNION ALL SELECT 'expenseWorkspace.taxType', 'Included tax method'
    UNION ALL SELECT 'expenseWorkspace.noTax', 'No included tax'
    UNION ALL SELECT 'expenseWorkspace.taxPercentage', 'Percentage'
    UNION ALL SELECT 'expenseWorkspace.taxFlat', 'Flat amount'
    UNION ALL SELECT 'expenseWorkspace.taxValue', 'Tax rate or amount'
    UNION ALL SELECT 'expenseWorkspace.taxLabel', 'Tax label (optional)'
    UNION ALL SELECT 'expenseWorkspace.taxInvalid', 'Included tax must be a 0–100% rate or a flat value no greater than the amount.'
    UNION ALL SELECT 'expenseWorkspace.reference', 'External reference (optional)'
    UNION ALL SELECT 'expenseWorkspace.notes', 'Business notes (optional)'
    UNION ALL SELECT 'expenseWorkspace.taxRule', 'Tax rule'
    UNION ALL SELECT 'expenseWorkspace.allocations', 'Employee allocations'
    UNION ALL SELECT 'expenseWorkspace.allocationCount', '{0} employees'
    UNION ALL SELECT 'expenseWorkspace.employeeDetail', 'Employee'
    UNION ALL SELECT 'expenseWorkspace.projectDetail', 'Project'
    UNION ALL SELECT 'expenseWorkspace.referenceDetail', 'External reference'
    UNION ALL SELECT 'expenseWorkspace.notesDetail', 'Business notes'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

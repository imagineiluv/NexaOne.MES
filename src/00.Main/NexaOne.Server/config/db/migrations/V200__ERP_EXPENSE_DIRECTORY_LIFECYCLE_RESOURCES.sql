-- Expense directory edit and active-lifecycle workspace UI resources. No role or business grants are seeded here.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'expenseWorkspace.edit' AS RESOURCE_KEY, 'Edit' AS VALUE
    UNION ALL SELECT 'expenseWorkspace.noDirectoryCategories', 'No categories.'
    UNION ALL SELECT 'expenseWorkspace.noDirectoryVendors', 'No vendors.'
    UNION ALL SELECT 'expenseWorkspace.editCategory', 'Edit category'
    UNION ALL SELECT 'expenseWorkspace.editVendor', 'Edit vendor'
    UNION ALL SELECT 'expenseWorkspace.name', 'Name'
    UNION ALL SELECT 'expenseWorkspace.phone', 'Phone'
    UNION ALL SELECT 'expenseWorkspace.website', 'Website'
    UNION ALL SELECT 'expenseWorkspace.email', 'Email'
    UNION ALL SELECT 'expenseWorkspace.inactiveHistoryHelp', 'Deactivation preserves existing expense references and audit history but prevents selection for new expenses.'
    UNION ALL SELECT 'expenseWorkspace.save', 'Save changes'
    UNION ALL SELECT 'expenseWorkspace.deactivate', 'Deactivate'
    UNION ALL SELECT 'expenseWorkspace.reactivate', 'Reactivate'
    UNION ALL SELECT 'expenseWorkspace.cancelEdit', 'Close editor'
    UNION ALL SELECT 'expenseWorkspace.active', 'Active'
    UNION ALL SELECT 'expenseWorkspace.inactive', 'Inactive'
    UNION ALL SELECT 'expenseWorkspace.directoryUpdated', 'Category updated.'
    UNION ALL SELECT 'expenseWorkspace.directoryVendorUpdated', 'Vendor updated.'
    UNION ALL SELECT 'expenseWorkspace.categoryActivated', 'Category reactivated.'
    UNION ALL SELECT 'expenseWorkspace.categoryDeactivated', 'Category deactivated.'
    UNION ALL SELECT 'expenseWorkspace.vendorActivated', 'Vendor reactivated.'
    UNION ALL SELECT 'expenseWorkspace.vendorDeactivated', 'Vendor deactivated.'
    UNION ALL SELECT 'expenseWorkspace.directoryInvalidResponse', 'The directory response does not match this request. The latest state was reloaded.'
    UNION ALL SELECT 'expenseWorkspace.directoryRecovered', 'The stored directory result was recovered.'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

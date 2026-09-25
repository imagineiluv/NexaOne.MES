-- Expense tag master and selector resources. Existing expense grants govern this workspace.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'expenseWorkspace.tagName' AS RESOURCE_KEY, 'New tag name' AS VALUE
    UNION ALL SELECT 'expenseWorkspace.addTag', 'Add tag'
    UNION ALL SELECT 'expenseWorkspace.tags', 'Tags'
    UNION ALL SELECT 'expenseWorkspace.noDirectoryTags', 'No tags.'
    UNION ALL SELECT 'expenseWorkspace.editTag', 'Edit tag'
    UNION ALL SELECT 'expenseWorkspace.tagSaved', 'Tag added.'
    UNION ALL SELECT 'expenseWorkspace.tagUpdated', 'Tag updated.'
    UNION ALL SELECT 'expenseWorkspace.tagActivated', 'Tag reactivated.'
    UNION ALL SELECT 'expenseWorkspace.tagDeactivated', 'Tag deactivated.'
    UNION ALL SELECT 'expenseWorkspace.tagSelector', 'Tags (optional)'
    UNION ALL SELECT 'expenseWorkspace.noTags', 'No active tags are available.'
    UNION ALL SELECT 'expenseWorkspace.tagPages', 'Tag pages'
    UNION ALL SELECT 'expenseWorkspace.tagSelectionHelp', 'Select multiple tags; selections remain while you browse pages.'
    UNION ALL SELECT 'expenseWorkspace.selectedTags', 'Selected tags'
    UNION ALL SELECT 'expenseWorkspace.removeTag', 'Remove tag {0}'
    UNION ALL SELECT 'expenseWorkspace.removeTagAction', 'Remove'
    UNION ALL SELECT 'expenseWorkspace.tagsDetail', 'Tags'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

-- Organization-scoped expense tags. Historical payloads retain tag IDs after deactivation;
-- new and updated expense values may reference active rows only.
CREATE TABLE ERP_EXPENSE_TAG (
    TENANT_ID VARCHAR(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
    ORGANIZATION_ID VARCHAR(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
    TAG_ID VARCHAR(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
    VERSION VARCHAR(36) COLLATE Latin1_General_100_BIN2 NOT NULL,
    NAME NVARCHAR(255) COLLATE Latin1_General_100_BIN2 NOT NULL,
    NAME_KEY NVARCHAR(255) COLLATE Latin1_General_100_BIN2 NOT NULL,
    IS_ACTIVE BIT NOT NULL,
    PAYLOAD NVARCHAR(MAX) NOT NULL,
    CONSTRAINT PK_ERP_EXPENSE_TAG PRIMARY KEY (TENANT_ID, ORGANIZATION_ID, TAG_ID),
    CONSTRAINT UQ_ERP_EXPENSE_TAG_NAME UNIQUE (TENANT_ID, ORGANIZATION_ID, NAME_KEY),
    CONSTRAINT CK_ERP_EXPENSE_TAG_ACTIVE CHECK (IS_ACTIVE IN (0,1))
);
CREATE INDEX IX_ERP_EXPENSE_TAG_PAGE ON ERP_EXPENSE_TAG
    (TENANT_ID, ORGANIZATION_ID, IS_ACTIVE, NAME, TAG_ID);

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

-- Delivery catalog workspace English resources. Missing rows only; preserve customized values.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'deliveryAdmin.active' AS RESOURCE_KEY, 'Active' AS VALUE
    UNION ALL SELECT 'deliveryAdmin.body', 'Body'
    UNION ALL SELECT 'deliveryAdmin.cancel', 'Cancel'
    UNION ALL SELECT 'deliveryAdmin.choose', 'Choose'
    UNION ALL SELECT 'deliveryAdmin.createdAt', 'Created at (UTC)'
    UNION ALL SELECT 'deliveryAdmin.createProfile', 'Create profile'
    UNION ALL SELECT 'deliveryAdmin.createTemplate', 'Create template'
    UNION ALL SELECT 'deliveryAdmin.credentialReference', 'Credential reference'
    UNION ALL SELECT 'deliveryAdmin.deactivate', 'Deactivate'
    UNION ALL SELECT 'deliveryAdmin.inactive', 'Inactive'
    UNION ALL SELECT 'deliveryAdmin.initialDelay', 'Initial delay (seconds)'
    UNION ALL SELECT 'deliveryAdmin.maximumDelay', 'Maximum delay (seconds)'
    UNION ALL SELECT 'deliveryAdmin.maxAttempts', 'Maximum attempts'
    UNION ALL SELECT 'deliveryAdmin.name', 'Name'
    UNION ALL SELECT 'deliveryAdmin.profile', 'Profile'
    UNION ALL SELECT 'deliveryAdmin.profiles', 'Provider profiles'
    UNION ALL SELECT 'deliveryAdmin.provider', 'Provider'
    UNION ALL SELECT 'deliveryAdmin.queue', 'Queue delivery'
    UNION ALL SELECT 'deliveryAdmin.recipient', 'Recipient'
    UNION ALL SELECT 'deliveryAdmin.refresh', 'Refresh all'
    UNION ALL SELECT 'deliveryAdmin.requestFailed', 'The request could not be completed.'
    UNION ALL SELECT 'deliveryAdmin.requests', 'Delivery requests'
    UNION ALL SELECT 'deliveryAdmin.retryPolicy', 'Retry policy'
    UNION ALL SELECT 'deliveryAdmin.secretNote', 'Template bodies and credential references are never shown in lists.'
    UNION ALL SELECT 'deliveryAdmin.state', 'State'
    UNION ALL SELECT 'deliveryAdmin.subject', 'Subject'
    UNION ALL SELECT 'deliveryAdmin.template', 'Template'
    UNION ALL SELECT 'deliveryAdmin.templates', 'Templates'
    UNION ALL SELECT 'deliveryAdmin.title', 'Delivery setup and requests'
    UNION ALL SELECT 'deliveryAdmin.variables', 'Variables'
    UNION ALL SELECT 'deliveryAdmin.variableValues', 'Variable values (one key=value per line)'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

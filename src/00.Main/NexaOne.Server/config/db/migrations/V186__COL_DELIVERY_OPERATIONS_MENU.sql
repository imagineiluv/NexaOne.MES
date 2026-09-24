-- Delivery dead-letter operations under the sales folder. No role or business grants are seeded.
-- SQLITE-OMIT-BEGIN
INSERT INTO SYS_MENU
    (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, UI_ID, VALID_STATE)
SELECT 'NX_DELIVERY_OPERATIONS', N'송달 실패 작업', 'FACTORY_SLS',
       (SELECT COALESCE(MAX(DISPLAY_SEQUENCE), 0) + CASE WHEN MAX(DISPLAY_SEQUENCE) = 2147483647 THEN 0 ELSE 1 END
          FROM SYS_MENU WHERE PARENT_MENU_ID = 'FACTORY_SLS'),
       'Screen', '', 'NX_DELIVERY_OPERATIONS', 'Valid'
WHERE EXISTS (SELECT 1 FROM SYS_MENU WHERE MENU_ID = 'FACTORY_SLS' AND MENU_TYPE = 'Folder')
  AND NOT EXISTS (SELECT 1 FROM SYS_MENU
                   WHERE UPPER(MENU_ID) = 'NX_DELIVERY_OPERATIONS' OR UPPER(UI_ID) = 'NX_DELIVERY_OPERATIONS');
-- SQLITE-OMIT-END

INSERT INTO SYS_MENU
    (MENU_ID, MENU_NAME, PARENT_MENU_ID, DISPLAY_SEQUENCE, MENU_TYPE, PROGRAM_ID, UI_ID, VALID_STATE)
SELECT 'NX_DELIVERY_OPERATIONS', '송달 실패 작업', 'FACTORY_SLS',
       (SELECT COALESCE(MAX(DISPLAY_SEQUENCE), 0) + CASE WHEN MAX(DISPLAY_SEQUENCE) = 2147483647 THEN 0 ELSE 1 END
          FROM SYS_MENU WHERE PARENT_MENU_ID = 'FACTORY_SLS'),
       'Screen', '', 'NX_DELIVERY_OPERATIONS', 'Valid'
WHERE EXISTS (SELECT 1 FROM SYS_MENU WHERE MENU_ID = 'FACTORY_SLS' AND MENU_TYPE = 'Folder')
  AND NOT EXISTS (SELECT 1 FROM SYS_MENU
                   WHERE UPPER(MENU_ID) = 'NX_DELIVERY_OPERATIONS' OR UPPER(UI_ID) = 'NX_DELIVERY_OPERATIONS');

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'menu.NX_DELIVERY_OPERATIONS' AS RESOURCE_KEY, 'Delivery failures' AS VALUE
    UNION ALL SELECT 'deliveryOps.actions', 'Actions'
    UNION ALL SELECT 'deliveryOps.attempts', 'Attempts'
    UNION ALL SELECT 'deliveryOps.createdAt', 'Created at (UTC)'
    UNION ALL SELECT 'deliveryOps.deadLetters', 'Dead letters'
    UNION ALL SELECT 'deliveryOps.discard', 'Discard'
    UNION ALL SELECT 'deliveryOps.empty', 'There are no dead letters to process.'
    UNION ALL SELECT 'deliveryOps.errorCode', 'Error code'
    UNION ALL SELECT 'deliveryOps.forbidden', 'You do not currently have permission.'
    UNION ALL SELECT 'deliveryOps.intro', 'Review dead letters in accessible organizations, retry their frozen payloads, or discard them.'
    UNION ALL SELECT 'deliveryOps.noScopes', 'No delivery scopes are currently accessible.'
    UNION ALL SELECT 'deliveryOps.organization', 'Organization'
    UNION ALL SELECT 'deliveryOps.provider', 'Provider'
    UNION ALL SELECT 'deliveryOps.readOnly', 'Read only'
    UNION ALL SELECT 'deliveryOps.recipient', 'Recipient'
    UNION ALL SELECT 'deliveryOps.refreshList', 'Refresh list'
    UNION ALL SELECT 'deliveryOps.refreshScopes', 'Refresh scopes'
    UNION ALL SELECT 'deliveryOps.requestFailed', 'The request could not be completed.'
    UNION ALL SELECT 'deliveryOps.retry', 'Retry'
    UNION ALL SELECT 'deliveryOps.retryNote', 'Retry starts a fresh attempt cycle. If the outcome is uncertain, press the same action again to recover with the same operation ID.'
    UNION ALL SELECT 'deliveryOps.scopes', 'My delivery scopes'
    UNION ALL SELECT 'deliveryOps.signIn', 'Check your session and sign in again.'
    UNION ALL SELECT 'deliveryOps.tenant', 'Tenant'
    UNION ALL SELECT 'deliveryOps.title', 'Delivery failure operations'
    UNION ALL SELECT 'deliveryOps.unavailable', 'The service is unavailable. Retry the same action.'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

-- Credit-note lifecycle workspace resources.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'billing.createCreditNote' AS RESOURCE_KEY, 'Create credit note' AS VALUE
    UNION ALL SELECT 'billing.updateCreditNote', 'Save credit note changes'
    UNION ALL SELECT 'billing.issueCreditNote', 'Issue credit note'
    UNION ALL SELECT 'billing.voidCreditNote', 'Void credit note'
    UNION ALL SELECT 'billing.creditContext', 'The selected invoice contact and currency are preserved. The credit note due date equals its document date.'
    UNION ALL SELECT 'billing.creditAvailable', 'Current available balance'
    UNION ALL SELECT 'billing.invalidCreditNote', 'A credit note must have a positive total and the same document and due date. Check its lines, discount and taxes.'
    UNION ALL SELECT 'billing.notCreditable', 'A credit note cannot be created for a draft or void invoice.'
    UNION ALL SELECT 'billing.creditContextMismatch', 'The credit note contact or currency differs from the invoice. Read the current state again.'
    UNION ALL SELECT 'billing.creditExceedsDue', 'The credit exceeds the invoice''s available balance. Check the invoice''s current due amount.'
    UNION ALL SELECT 'billing.notCreditNote', 'The selected document is not a credit note.'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                  WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

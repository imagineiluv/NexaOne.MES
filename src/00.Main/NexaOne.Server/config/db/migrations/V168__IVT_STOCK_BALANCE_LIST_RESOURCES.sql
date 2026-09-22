-- Warehouse balance list English resources; Korean text remains in the UI fallbacks.
-- Insert missing keys only, preserving existing values (including empty/custom translations) and MENU_ID.
-- No menu, role, permission or business-data changes.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE
FROM (
    SELECT 'inventory.stock.balanceListError' AS RESOURCE_KEY, 'The warehouse was selected, but its balance list could not be loaded. Search again.' AS VALUE
    UNION ALL SELECT 'inventory.stock.balanceListNote', 'Balances recorded for the selected warehouse. A balance that reached zero stays listed; a product with no ledger entry here is not shown. Variants are shown by their stored identifiers.'
    UNION ALL SELECT 'inventory.stock.balances', 'Warehouse balances'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

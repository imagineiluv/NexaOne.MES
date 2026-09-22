-- Variant selection English resources; Korean text remains in the UI fallbacks.
-- Insert missing keys only, preserving existing values (including empty/custom translations) and MENU_ID.
-- No menu, role, permission or business-data changes.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE
FROM (
    SELECT 'inventory.stock.chooseVariant' AS RESOURCE_KEY, 'Select product' AS VALUE
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

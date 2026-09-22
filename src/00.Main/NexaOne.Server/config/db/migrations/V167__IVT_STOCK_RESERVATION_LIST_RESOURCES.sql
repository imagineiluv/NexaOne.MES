-- Stock reservation list and balance lookup English resources; Korean text remains in the UI fallbacks.
-- Insert missing keys only, preserving existing values (including empty/custom translations) and MENU_ID.
-- No menu, role, permission or business-data changes.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE
FROM (
    SELECT 'inventory.stock.available' AS RESOURCE_KEY, 'Available' AS VALUE
    UNION ALL SELECT 'inventory.stock.balanceWarehouse', 'Balance warehouse'
    UNION ALL SELECT 'inventory.stock.chooseReservation', 'Select reservation'
    UNION ALL SELECT 'inventory.stock.invalidBalanceWarehouse', 'Select an active warehouse to read its balance.'
    UNION ALL SELECT 'inventory.stock.noBalance', 'No recorded balance (0). This warehouse has no ledger entries for the product yet.'
    UNION ALL SELECT 'inventory.stock.onHand', 'On hand'
    UNION ALL SELECT 'inventory.stock.readBalance', 'Read balance'
    UNION ALL SELECT 'inventory.stock.reservationListNote', 'Reservations of the selected product variant, ordered by reservation ID rather than creation time. Select a row to release or consume it in Stock actions.'
    UNION ALL SELECT 'inventory.stock.reservations', 'Reservations'
    UNION ALL SELECT 'inventory.stock.reserved', 'Reserved'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

-- Current stock balance report workspace resources.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'inventory.stock.reportHeading' AS RESOURCE_KEY, 'Current stock balance report' AS VALUE
    UNION ALL SELECT 'inventory.stock.reportIntro', 'Review recorded on-hand, reserved and available quantities for the whole scope or current selections, then export them as CSV.'
    UNION ALL SELECT 'inventory.stock.reportRun', 'Load current balances'
    UNION ALL SELECT 'inventory.stock.reportDownload', 'Download CSV for these filters'
    UNION ALL SELECT 'inventory.stock.reportFilters', 'Report filters'
    UNION ALL SELECT 'inventory.stock.reportSelectedWarehouse', 'Current selected warehouse only'
    UNION ALL SELECT 'inventory.stock.reportSelectedVariant', 'Current selected product only'
    UNION ALL SELECT 'inventory.stock.reportLoading', 'Loading the stock balance report…'
    UNION ALL SELECT 'inventory.stock.reportRows', 'Report rows'
    UNION ALL SELECT 'inventory.stock.reportGeneratedAt', 'Generated at (UTC)'
    UNION ALL SELECT 'inventory.stock.reportEmpty', 'No recorded stock balances match these filters.'
    UNION ALL SELECT 'inventory.stock.reportWarehouse', 'Warehouse'
    UNION ALL SELECT 'inventory.stock.reportProduct', 'Product'
    UNION ALL SELECT 'inventory.stock.reportUnit', 'Unit'
    UNION ALL SELECT 'inventory.stock.reportPages', 'Stock balance report pages'
    UNION ALL SELECT 'inventory.stock.reportInitial', 'Review the filters, then load current balances. CSV download remains unavailable until a report is loaded.'
    UNION ALL SELECT 'inventory.stock.reportInvalidResponse', 'The server report does not match the selected scope or filters. Refresh the scope and try again.'
    UNION ALL SELECT 'inventory.stock.reportInvalidDownload', 'The server did not return a valid CSV file. Reload the report before downloading.'
    UNION ALL SELECT 'inventory.stock.reportDownloadStarted', 'CSV download started.'
    UNION ALL SELECT 'inventory.stock.reportDownloadFailed', 'The browser could not start the CSV download. Try again.'
    UNION ALL SELECT 'inventory.stock.reportTooLarge', 'The result exceeds 10,000 rows. Apply a warehouse or product filter and try again.'
    UNION ALL SELECT 'inventory.stock.reportFailed', 'The stock balance report could not be loaded. Try again.'
    UNION ALL SELECT 'inventory.stock.reportNoWarehouse', 'Select a warehouse in Stock actions first.'
    UNION ALL SELECT 'inventory.stock.reportNoVariant', 'Select a product in Stock actions first.'
    UNION ALL SELECT 'inventory.stock.reportAllScope', 'Applied filters: entire scope'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                  WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

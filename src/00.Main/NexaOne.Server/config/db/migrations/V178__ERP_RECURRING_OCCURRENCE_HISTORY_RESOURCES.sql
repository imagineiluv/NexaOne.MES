-- Recurring occurrence history English resources. Missing rows only; preserve customized values.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'recurring.clearRuleFilter' AS RESOURCE_KEY, 'Show all rule history' AS VALUE
    UNION ALL SELECT 'recurring.createdBy', 'Created by'
    UNION ALL SELECT 'recurring.fromMonth', 'From month'
    UNION ALL SELECT 'recurring.history', 'Occurrence history'
    UNION ALL SELECT 'recurring.historyEmpty', 'No persisted occurrences match these filters.'
    UNION ALL SELECT 'recurring.historyForRule', 'History for selected rule'
    UNION ALL SELECT 'recurring.historyNote', 'Persisted monthly results are listed newest first.'
    UNION ALL SELECT 'recurring.invalidHistoryRange', 'Check the occurrence-history start and end months.'
    UNION ALL SELECT 'recurring.persistedResult', 'This result is persisted and available in occurrence history.'
    UNION ALL SELECT 'recurring.refreshHistory', 'Refresh history'
    UNION ALL SELECT 'recurring.toMonth', 'To month'
    UNION ALL SELECT 'recurring.viewHistory', 'View history'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
    WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

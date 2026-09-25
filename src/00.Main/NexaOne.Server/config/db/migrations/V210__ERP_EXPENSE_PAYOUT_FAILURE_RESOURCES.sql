-- Failed payout operations workspace resources.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'expenseWorkspace.failedPayouts' AS RESOURCE_KEY, 'Failed payout operations' AS VALUE
    UNION ALL SELECT 'expenseWorkspace.failedPayoutsHelp', 'Return failed payouts to pending under the same payout ID, or discard them while preserving failure evidence. If the outcome is uncertain, press the same action again to recover with the same operation ID.'
    UNION ALL SELECT 'expenseWorkspace.refreshFailedPayouts', 'Refresh failed payouts'
    UNION ALL SELECT 'expenseWorkspace.noFailedPayouts', 'There are no failed payouts to process.'
    UNION ALL SELECT 'expenseWorkspace.retryFailedPayout', 'Retry'
    UNION ALL SELECT 'expenseWorkspace.discardFailedPayout', 'Discard'
    UNION ALL SELECT 'expenseWorkspace.payoutReadOnly', 'Read only'
    UNION ALL SELECT 'expenseWorkspace.failedPayoutInvalidResponse', 'The failed-payout response contains an ineligible state. Refresh the list.'
    UNION ALL SELECT 'expenseWorkspace.failedPayoutRetried', 'The failed payout was moved to a fresh attempt cycle.'
    UNION ALL SELECT 'expenseWorkspace.failedPayoutDiscarded', 'The failed payout was discarded.'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                  WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

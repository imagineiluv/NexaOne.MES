-- Expense payout request, status, failure and cancellation resources.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'expenseWorkspace.payout' AS RESOURCE_KEY, 'Employee payout' AS VALUE
    UNION ALL SELECT 'expenseWorkspace.payoutHelp', 'Request reimbursement through a host-registered payout provider key. Account and credential details are neither entered nor stored here.'
    UNION ALL SELECT 'expenseWorkspace.refreshPayout', 'Refresh payout status'
    UNION ALL SELECT 'expenseWorkspace.noPayouts', 'This expense has no payout requests.'
    UNION ALL SELECT 'expenseWorkspace.payoutUnavailable', 'Payout status could not be confirmed, so conflicting actions are locked. Refresh to try again.'
    UNION ALL SELECT 'expenseWorkspace.payoutCreated', 'Requested'
    UNION ALL SELECT 'expenseWorkspace.payoutAttempts', 'Attempts'
    UNION ALL SELECT 'expenseWorkspace.payoutOperation', 'Operation ID'
    UNION ALL SELECT 'expenseWorkspace.payoutFailure', 'Failure code'
    UNION ALL SELECT 'expenseWorkspace.payoutReference', 'Provider reference'
    UNION ALL SELECT 'expenseWorkspace.payoutPaidAt', 'Paid at'
    UNION ALL SELECT 'expenseWorkspace.cancelPayout', 'Cancel pending payout'
    UNION ALL SELECT 'expenseWorkspace.payoutProvider', 'Payout provider key'
    UNION ALL SELECT 'expenseWorkspace.queuePayout', 'Queue payout'
    UNION ALL SELECT 'expenseWorkspace.retryPayout', 'Retry same payout request'
    UNION ALL SELECT 'expenseWorkspace.payoutProcessingLock', 'The provider is processing this payout. Expense changes and manual reimbursement remain unavailable until completion or failure is confirmed.'
    UNION ALL SELECT 'expenseWorkspace.payoutProviderRequired', 'Enter a payout provider key.'
    UNION ALL SELECT 'expenseWorkspace.payoutInvalidResponse', 'The payout response does not match this expense. Refresh the payout status.'
    UNION ALL SELECT 'expenseWorkspace.payoutRecovered', 'The stored payout request was recovered.'
    UNION ALL SELECT 'expenseWorkspace.payoutQueued', 'Employee payout queued.'
    UNION ALL SELECT 'expenseWorkspace.payoutCancelled', 'Pending payout cancelled.'
    UNION ALL SELECT 'expenseWorkspace.payoutPending', 'Pending'
    UNION ALL SELECT 'expenseWorkspace.payoutProcessing', 'Processing'
    UNION ALL SELECT 'expenseWorkspace.payoutCompleted', 'Completed'
    UNION ALL SELECT 'expenseWorkspace.payoutFailed', 'Failed'
    UNION ALL SELECT 'expenseWorkspace.payoutCancelledState', 'Cancelled'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                  WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

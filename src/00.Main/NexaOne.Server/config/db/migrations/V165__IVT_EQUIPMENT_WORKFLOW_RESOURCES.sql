-- Equipment workflow English resources; Korean text remains in the UI fallbacks.
-- Insert missing keys only, preserving existing values (including empty/custom translations) and MENU_ID.
-- No menu, role, permission or business-data changes.
-- SQL Server Unicode pre-inserts; SQLite uses the portable rows below.
-- SQLITE-OMIT-BEGIN
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE
FROM (
    SELECT 'inventory.write.confirmedRecovery' AS RESOURCE_KEY, N'Write confirmed · clear recovery record' AS VALUE
    UNION ALL SELECT 'inventory.write.loadingRecovery', N'Checking this tab''s earlier request…'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');
-- SQLITE-OMIT-END

INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE
FROM (
    SELECT 'error.inventoryIdentityChanged' AS RESOURCE_KEY, 'The signed-in user changed or could not be verified. Sign in as the original user before continuing.' AS VALUE
    UNION ALL SELECT 'error.inventoryWriteOutcome', 'The write outcome could not be confirmed. Check the current state before retrying.'
    UNION ALL SELECT 'error.inventoryWriteRequest', 'The inventory write request is invalid.'
    UNION ALL SELECT 'inventory.write.accessChanged', 'Authentication or permission changed. Check your session and business scope.'
    UNION ALL SELECT 'inventory.write.acknowledge', 'I checked the current state separately and will not execute this request again.'
    UNION ALL SELECT 'inventory.write.actions', 'Actions'
    UNION ALL SELECT 'inventory.write.activate', 'Activate equipment'
    UNION ALL SELECT 'inventory.write.adoptCurrent', 'Start from loaded state'
    UNION ALL SELECT 'inventory.write.approve', 'Approve booking'
    UNION ALL SELECT 'inventory.write.assetInactive', 'Inactive equipment cannot receive a new booking or check-out.'
    UNION ALL SELECT 'inventory.write.assetReadRequired', 'Selecting equipment for a booking requires equipment read permission. Request permission does not grant access to the equipment list.'
    UNION ALL SELECT 'inventory.write.bookingAction', 'Selected booking'
    UNION ALL SELECT 'inventory.write.bookingReadRequired', 'Selecting an existing booking requires booking read permission. Action and read permissions are independent.'
    UNION ALL SELECT 'inventory.write.bookingWorker', 'Booking worker'
    UNION ALL SELECT 'inventory.write.cancel', 'Cancel booking'
    UNION ALL SELECT 'inventory.write.capacityUnavailable', 'Equipment capacity is unavailable for that interval. Change the dates or quantity.'
    UNION ALL SELECT 'inventory.write.changeActivity', 'Change equipment activity'
    UNION ALL SELECT 'inventory.write.checkout', 'Check out equipment'
    UNION ALL SELECT 'inventory.write.checkoutWindow', 'Check-out is available from the booking start until before its end.'
    UNION ALL SELECT 'inventory.write.chooseAsset', 'Select equipment'
    UNION ALL SELECT 'inventory.write.chooseBooking', 'Select booking'
    UNION ALL SELECT 'inventory.write.chooseWorker', 'Select from the worker list below.'
    UNION ALL SELECT 'inventory.write.chooseWorkerAction', 'Select booking worker'
    UNION ALL SELECT 'inventory.write.closeRecovery', 'Close checked recovery record'
    UNION ALL SELECT 'inventory.write.compareCurrent', 'Current state for comparison'
    UNION ALL SELECT 'inventory.write.compareNote', 'Your draft and request version are preserved. The button below starts editing from the loaded state without saving.'
    UNION ALL SELECT 'inventory.write.confirmedRecovery', 'Write confirmed · clear recovery record'
    UNION ALL SELECT 'inventory.write.conflict', 'The request conflicts with current data. Check the input and current state.'
    UNION ALL SELECT 'inventory.write.createAsset', 'Register equipment'
    UNION ALL SELECT 'inventory.write.currentRead', 'Current state loaded. This alone does not prove whether the earlier request executed.'
    UNION ALL SELECT 'inventory.write.deactivate', 'Deactivate equipment'
    UNION ALL SELECT 'inventory.write.deny', 'Deny booking'
    UNION ALL SELECT 'inventory.write.heading', 'Equipment actions'
    UNION ALL SELECT 'inventory.write.invalidInput', 'Check required code/name, positive quantities, and a UTC end later than the start.'
    UNION ALL SELECT 'inventory.write.invalidResponse', 'The response does not match the selected scope or request. Recheck the write outcome.'
    UNION ALL SELECT 'inventory.write.loadingRecovery', 'Checking this tab''s earlier request…'
    UNION ALL SELECT 'inventory.write.notFound', 'The record was not found. Check the current list and business scope.'
    UNION ALL SELECT 'inventory.write.openBookings', 'Open bookings prevent this equipment change.'
    UNION ALL SELECT 'inventory.write.operationConflict', 'This request ID has different recorded input. Check the original request and current state.'
    UNION ALL SELECT 'inventory.write.originalRequest', 'View original request'
    UNION ALL SELECT 'inventory.write.ownerRequired', 'Refresh your current user and business scope before taking action.'
    UNION ALL SELECT 'inventory.write.pendingRecovery', 'Request outcome needs checking'
    UNION ALL SELECT 'inventory.write.prepareBooking', 'Book selected equipment'
    UNION ALL SELECT 'inventory.write.readCurrent', 'Read current state'
    UNION ALL SELECT 'inventory.write.recoveryClosed', 'This tab''s recovery record was closed. Server data was not changed.'
    UNION ALL SELECT 'inventory.write.reloadRecovery', 'Reload recovery record'
    UNION ALL SELECT 'inventory.write.requestBooking', 'Request booking'
    UNION ALL SELECT 'inventory.write.requestId', 'Request/record ID'
    UNION ALL SELECT 'inventory.write.retrySame', 'Recheck same request'
    UNION ALL SELECT 'inventory.write.return', 'Return equipment'
    UNION ALL SELECT 'inventory.write.saveAsset', 'Save equipment changes'
    UNION ALL SELECT 'inventory.write.saved', 'Saved. The current returned state is shown.'
    UNION ALL SELECT 'inventory.write.savedRecoveryError', 'The write succeeded, but its recovery record could not be cleared. Check the record again.'
    UNION ALL SELECT 'inventory.write.savedRefreshError', 'The write succeeded, but the lists could not be refreshed. Search again.'
    UNION ALL SELECT 'inventory.write.selectHint', 'Select an equipment or booking row below to act. For a new booking, select equipment and a worker first.'
    UNION ALL SELECT 'inventory.write.selfDecision', 'You cannot approve or deny your own request.'
    UNION ALL SELECT 'inventory.write.sending', 'Processing the request. Wait for its outcome before sending again.'
    UNION ALL SELECT 'inventory.write.stateConflict', 'This action is unavailable in the current booking state. Reload the booking.'
    UNION ALL SELECT 'inventory.write.storageError', 'The recovery record could not be read or saved. New requests are blocked until it is checked.'
    UNION ALL SELECT 'inventory.write.tabRecovery', 'Recovery is kept in this browser tab only. Closing the record does not undo saved business data.'
    UNION ALL SELECT 'inventory.write.timeError', 'Check the UTC booking interval and current time.'
    UNION ALL SELECT 'inventory.write.unknown', 'The outcome is unknown. Recheck the same request or read the current state.'
    UNION ALL SELECT 'inventory.write.utcNote', 'All times below are UTC. Enter them separately from the plant''s local time.'
    UNION ALL SELECT 'inventory.write.versionConflict', 'The version changed. Read the current state before deciding what to do next.'
    UNION ALL SELECT 'inventory.write.workerNote', 'Select an active worker in the current plant, then request a booking in Equipment actions. Selection alone does not save anything.'
    UNION ALL SELECT 'inventory.write.workerSelected', 'Selected booking worker'
    UNION ALL SELECT 'inventory.write.workerUnavailable', 'The worker is unavailable in the current plant. Refresh the worker list.'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

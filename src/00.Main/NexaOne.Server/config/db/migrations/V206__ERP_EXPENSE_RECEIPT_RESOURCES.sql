-- Expense receipt upload, download, replacement and explicit deletion resources.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'expenseWorkspace.receiptFile' AS RESOURCE_KEY, 'Receipt file' AS VALUE
    UNION ALL SELECT 'expenseWorkspace.receiptFileHelp', 'Store a PDF, JPG, PNG, or WebP file up to 10 MB. Cancelling the expense keeps the file until it is explicitly deleted.'
    UNION ALL SELECT 'expenseWorkspace.noReceiptFile', 'No receipt file is stored.'
    UNION ALL SELECT 'expenseWorkspace.downloadReceipt', 'Download receipt'
    UNION ALL SELECT 'expenseWorkspace.chooseReceipt', 'File to upload'
    UNION ALL SELECT 'expenseWorkspace.chooseReplacementReceipt', 'Replacement file'
    UNION ALL SELECT 'expenseWorkspace.receiptConstraints', 'Accepted: PDF, JPG, PNG, WebP · 10 MB maximum'
    UNION ALL SELECT 'expenseWorkspace.uploadReceipt', 'Upload receipt'
    UNION ALL SELECT 'expenseWorkspace.replaceReceipt', 'Replace receipt'
    UNION ALL SELECT 'expenseWorkspace.deleteReceiptConfirm', 'Permanently delete the stored receipt file? The expense record will remain.'
    UNION ALL SELECT 'expenseWorkspace.confirmDeleteReceipt', 'Delete receipt file'
    UNION ALL SELECT 'expenseWorkspace.keepReceipt', 'Keep file'
    UNION ALL SELECT 'expenseWorkspace.deleteReceipt', 'Delete receipt'
    UNION ALL SELECT 'expenseWorkspace.receiptReadOnly', 'In this scope, you can only view and download receipts.'
    UNION ALL SELECT 'expenseWorkspace.receiptInvalidResponse', 'The receipt response does not match the selected expense. Select the expense again.'
    UNION ALL SELECT 'expenseWorkspace.receiptFileInvalid', 'Choose a PDF, JPG, PNG, or WebP file no larger than 10 MB.'
    UNION ALL SELECT 'expenseWorkspace.receiptUploaded', 'Receipt file saved.'
    UNION ALL SELECT 'expenseWorkspace.receiptDownloadFailed', 'The browser could not start the receipt download. Try again.'
    UNION ALL SELECT 'expenseWorkspace.receiptDeleted', 'Receipt file deleted.'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                  WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

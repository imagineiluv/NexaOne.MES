-- Expense-to-invoice link workspace UI resources. No role or business grants are seeded here.
INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
SELECT seed.RESOURCE_KEY, 'COMMON', 'EnUs', seed.VALUE FROM (
    SELECT 'expenseWorkspace.invoiceLink' AS RESOURCE_KEY, 'Invoice link' AS VALUE
    UNION ALL SELECT 'expenseWorkspace.invoiceLinkHelp', 'Link this expense as one line on a draft invoice with the same contact and currency.'
    UNION ALL SELECT 'expenseWorkspace.linkedInvoice', 'Linked invoice'
    UNION ALL SELECT 'expenseWorkspace.retryInvoiceUnlink', 'Retry same unlink request'
    UNION ALL SELECT 'expenseWorkspace.unlinkInvoice', 'Unlink invoice'
    UNION ALL SELECT 'expenseWorkspace.invoiceLocked', 'Only a draft invoice can be unlinked.'
    UNION ALL SELECT 'expenseWorkspace.loadInvoices', 'Load compatible invoices'
    UNION ALL SELECT 'expenseWorkspace.chooseInvoice', 'Draft invoice'
    UNION ALL SELECT 'expenseWorkspace.chooseInvoicePlaceholder', 'Choose invoice'
    UNION ALL SELECT 'expenseWorkspace.noCompatibleInvoices', 'This page has no draft invoices with the same contact and currency.'
    UNION ALL SELECT 'expenseWorkspace.previousInvoices', 'Previous invoices'
    UNION ALL SELECT 'expenseWorkspace.invoicePage', '{0}–{1} of {2}'
    UNION ALL SELECT 'expenseWorkspace.nextInvoices', 'Next invoices'
    UNION ALL SELECT 'expenseWorkspace.invoiceDescription', 'Invoice line description (optional)'
    UNION ALL SELECT 'expenseWorkspace.retryInvoiceLink', 'Retry same link request'
    UNION ALL SELECT 'expenseWorkspace.linkInvoice', 'Link selected invoice'
    UNION ALL SELECT 'expenseWorkspace.billingReadRequired', 'The billing.read permission is required to select or unlink an invoice.'
    UNION ALL SELECT 'expenseWorkspace.chooseInvoiceRequired', 'Choose a compatible invoice to link.'
    UNION ALL SELECT 'expenseWorkspace.invoiceInvalidResponse', 'The invoice-link response does not match this request. Refresh the expense and invoice.'
    UNION ALL SELECT 'expenseWorkspace.invoiceLinked', 'Expense linked to invoice.'
    UNION ALL SELECT 'expenseWorkspace.invoiceUnlinked', 'Expense unlinked from invoice.'
) seed
WHERE NOT EXISTS (SELECT 1 FROM SYS_MULTI_LANGUAGE_RESOURCE existing
                   WHERE existing.RESOURCE_KEY = seed.RESOURCE_KEY AND existing.LANGUAGE = 'EnUs');

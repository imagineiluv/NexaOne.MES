-- Preserve the original create request independently of editable billing fields.
-- Existing rows remain NULL: their original request cannot be reconstructed after prior edits.
ALTER TABLE ERP_BILLING_DOCUMENT ADD CREATION_INPUT_JSON NVARCHAR(MAX) NULL;

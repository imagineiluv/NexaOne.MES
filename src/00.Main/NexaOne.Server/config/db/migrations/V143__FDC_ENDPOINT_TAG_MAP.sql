-- Owner: FDC. Atomic PLC subscription mapping and persisted interlock runtime guards.
-- NexaLogic real PLC drivers resolve logical FDC parameter IDs through a JSON tag catalog.
-- Nullable upgrade preserves existing rows; Worker:Fdc:Enabled=true rejects null/missing files before connection.
ALTER TABLE FDC_EQUIPMENT_ENDPOINT
    ADD TAG_MAP_PATH NVARCHAR(1000) NULL;

-- Existing parameter rows remain readable, but Worker:Fdc:Enabled=true requires every active row
-- to reference exactly one active endpoint before opening a PLC connection.
ALTER TABLE FDC_PARAMETER
    ADD ENDPOINT_ID NVARCHAR(50) NULL;

-- SQLITE-OMIT-BEGIN
ALTER TABLE FDC_PARAMETER
    ADD CONSTRAINT FK_FDC_PARAMETER_ENDPOINT FOREIGN KEY (ENDPOINT_ID)
        REFERENCES FDC_EQUIPMENT_ENDPOINT (ENDPOINT_ID);

ALTER TABLE FDC_INTERLOCK_RULE
    ADD CONSTRAINT CK_FDC_INTERLOCK_RULE_RUNTIME CHECK (
        LEN(LTRIM(RTRIM(RULE_ID))) > 0
        AND LEN(LTRIM(RTRIM(EQUIPMENT_ID))) > 0
        AND LEN(LTRIM(RTRIM(PARAMETER_ID))) > 0
        AND LEN(LTRIM(RTRIM(ACTION))) > 0
        AND OPERATOR IN ('GT', 'LT', 'GTE', 'LTE', 'EQ')
        AND PRIORITY >= 0
    );
-- SQLITE-OMIT-END

CREATE INDEX IX_FDC_PARAMETER_ENDPOINT_ACTIVE
    ON FDC_PARAMETER (ENDPOINT_ID, IS_ACTIVE);

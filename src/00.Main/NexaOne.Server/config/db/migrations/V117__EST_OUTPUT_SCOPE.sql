-- Distinguish LOT output from non-LOT equipment output during the transition from legacy
-- POM_LOT_HISTORY to the canonical EST output ledger. OEE can combine carrier/tool output with
-- legacy LOT TrackOut without dropping either source or double-counting the LOT domain.
ALTER TABLE EST_EQUIPMENT_OUTPUT_EVENT ADD
    IS_LOT_OUTPUT BIT NULL;

-- Existing canonical rows predate the explicit flag. Process-lot identity is the only durable
-- evidence available for that one-time classification; all new writes provide the flag explicitly.
UPDATE EST_EQUIPMENT_OUTPUT_EVENT
SET IS_LOT_OUTPUT = CASE WHEN PROCESS_LOT_ID IS NULL THEN 0 ELSE 1 END
WHERE IS_LOT_OUTPUT IS NULL;

ALTER TABLE EST_EQUIPMENT_OUTPUT_EVENT
    ALTER COLUMN IS_LOT_OUTPUT BIT NOT NULL;

ALTER TABLE EST_EQUIPMENT_OUTPUT_EVENT ADD CONSTRAINT CK_EST_EQUIPMENT_OUTPUT_SCOPE CHECK (
    IS_LOT_OUTPUT IN (0, 1)
    AND (IS_LOT_OUTPUT = 0 OR PROCESS_LOT_ID IS NOT NULL)
);

CREATE INDEX IX_EST_EQUIPMENT_OUTPUT_SCOPE
    ON EST_EQUIPMENT_OUTPUT_EVENT (EQUIPMENT_ID, IS_LOT_OUTPUT, OCCURRED_AT);

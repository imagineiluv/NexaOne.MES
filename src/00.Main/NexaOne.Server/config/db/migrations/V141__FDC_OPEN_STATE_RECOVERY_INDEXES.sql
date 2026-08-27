-- Owner: FDC. Process-restart recovery reads one equipment/parameter open state before
-- evaluating the first Good sample. Filtered indexes avoid scanning closed history and keep
-- steady-state writes limited to currently open alarm/interlock rows.

CREATE INDEX IX_FDC_INTERLOCK_OPEN_EQUIPMENT_PARAMETER
    ON FDC_INTERLOCK_HISTORY (EQUIPMENT_ID, PARAMETER_ID, TRIGGERED_AT DESC)
    WHERE IS_RESOLVED = 0;

CREATE INDEX IX_FDC_ALARM_OPEN_EQUIPMENT_PARAMETER
    ON FDC_ALARM_HISTORY (EQUIPMENT_ID, PARAMETER_ID, OCCURRED_AT DESC)
    WHERE IS_CLEARED = 0;

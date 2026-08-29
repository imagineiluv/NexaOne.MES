-- Owner: POM. OEE reads completed TrackOut evidence by plant/equipment/time window. The legacy
-- IX_POM_LOT_HISTORY_EQP places unconstrained TRACK_IN_TIME before TRACK_OUT_TIME and therefore
-- cannot provide this range seek. Keep the index filtered to completed TrackOut evidence.
-- SQLITE-OMIT-BEGIN
CREATE INDEX IX_POM_LOT_HISTORY_OEE_TRACK_OUT
    ON POM_LOT_HISTORY (PLANT_ID, EQUIPMENT_ID, TRACK_OUT_TIME)
    INCLUDE (LOT_ID, PROCESS_ID, QTY, DEFECT_QTY, TRACK_IN_TIME)
    WHERE EXECUTION_ID = 'TrackOut' AND TRACK_OUT_TIME IS NOT NULL;
-- SQLITE-OMIT-END

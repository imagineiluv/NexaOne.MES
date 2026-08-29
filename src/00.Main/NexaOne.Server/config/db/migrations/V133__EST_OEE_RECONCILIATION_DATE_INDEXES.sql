-- Owner: EST. Daily OEE reconciliation reads generated rows for a date window across all
-- equipment.  Existing equipment/plant-first indexes force a broad scan when
-- no equipment or plant is supplied, so keep a narrow date-first path per mart.
CREATE INDEX IX_EST_TAKT_RECONCILIATION_DATE
    ON EST_TAKT_SUMMARY (TAKT_DATE, TAKT_SUMMARY_ID);

CREATE INDEX IX_EST_OEE_LOSS_RECONCILIATION_DATE
    ON EST_OEE_LOSS (OEE_DATE, LOSS_ID);

CREATE INDEX IX_EST_OEE_SUMMARY_RECONCILIATION_DATE
    ON EST_OEE_SUMMARY (OEE_DATE, OEE_ID);

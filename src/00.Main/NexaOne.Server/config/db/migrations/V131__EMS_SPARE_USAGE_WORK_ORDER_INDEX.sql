-- Owner: EMS. EMS.SparePartUsageByWorkOrder has a required WO_ID equality and
-- optional usage-time bounds. Existing PART_ID/EQUIPMENT_ID indexes do not serve
-- that contract. Keep the index filtered because WO_ID is optional in the ledger.
CREATE INDEX IX_EMS_SPARE_USAGE_WO_TIME
    ON EMS_SPARE_PART_USAGE (WO_ID, USED_AT DESC)
    WHERE WO_ID IS NOT NULL;

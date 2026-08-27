-- Owner: EMS. Maintenance/tool query-path indexes backed by repository and named-query contracts.

-- ToolService validates the last usage timestamp for a mount before unmounting it.
CREATE INDEX IX_EMS_TOOL_USAGE_MOUNT
    ON EMS_TOOL_USAGE_HISTORY (MOUNT_ID, USED_AT DESC);

-- WorkOrderRepository and EMS.WorkOrder* read one equipment over an issued-time range/order.
-- Keep V008's (EQUIPMENT_ID, STATUS) index for status counts; this index owns the time cursor.
CREATE INDEX IX_EMS_WO_EQUIPMENT_ISSUED
    ON EMS_WORK_ORDER (EQUIPMENT_ID, ISSUED_AT DESC);

-- UQ_EMS_WORK_ORDER_CHECK_SEQUENCE already owns a unique (WO_ID, ITEM_SEQUENCE) index. The explicit
-- non-unique copy from V115 adds identical write cost without a distinct access path.
-- SQLITE-OMIT-BEGIN
DROP INDEX IX_EMS_WORK_ORDER_CHECK_RESULT_WO ON EMS_WORK_ORDER_CHECK_RESULT;
-- SQLITE-OMIT-END

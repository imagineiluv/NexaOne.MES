-- Owner: POM. Plant LOT screens and LotRepository both list by creation time. Preserve the
-- existing PLANT_ID/LOT_STATE index for selective state filters and add the
-- stable list path separately.
CREATE INDEX IX_POM_LOT_PLANT_CREATED
    ON POM_LOT (PLANT_ID, CREATED_AT DESC, LOT_ID);

-- SmartUX permits an all-plant dashboard read. Its result is capped at 500 rows and uses this
-- global newest-first cursor instead of sorting the full LOT master.
CREATE INDEX IX_POM_LOT_CREATED
    ON POM_LOT (CREATED_AT DESC, LOT_ID);

-- Fixed hold/defect screens are naturally sparse. Filtered indexes keep their write cost and
-- working set smaller while matching the exact bounded named-query order.
CREATE INDEX IX_POM_LOT_HOLD_CREATED
    ON POM_LOT (CREATED_AT DESC, LOT_ID)
    WHERE IS_HOLD = 'Y';

CREATE INDEX IX_POM_LOT_DEFECT_QTY
    ON POM_LOT (DEFECT_QTY DESC, CREATED_AT DESC, LOT_ID)
    WHERE DEFECT_QTY > 0;

-- POM.WorkOrderList also supports an all-plant dashboard read.
CREATE INDEX IX_POM_WORK_ORDER_PLAN_START
    ON POM_WORK_ORDER (PLAN_START_DATE DESC, WORK_ORDER_ID);

-- Disposition history always has a plant scope and is displayed newest-first.
-- LOT/type-specific indexes remain useful for their narrower branches.
CREATE INDEX IX_POM_LOT_DISPOSITION_PLANT_DATE
    ON POM_LOT_DISPOSITION (PLANT_ID, DECIDED_AT DESC, DISPOSITION_ID DESC);

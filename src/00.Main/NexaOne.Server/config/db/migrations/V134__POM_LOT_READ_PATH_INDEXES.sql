-- Owner: POM. Plant LOT screens and LotRepository both list by creation time. Preserve the
-- existing PLANT_ID/LOT_STATE index for selective state filters and add the
-- stable list path separately.
CREATE INDEX IX_POM_LOT_PLANT_CREATED
    ON POM_LOT (PLANT_ID, CREATED_AT DESC, LOT_ID);

-- Disposition history always has a plant scope and is displayed newest-first.
-- LOT/type-specific indexes remain useful for their narrower branches.
CREATE INDEX IX_POM_LOT_DISPOSITION_PLANT_DATE
    ON POM_LOT_DISPOSITION (PLANT_ID, DECIDED_AT DESC, DISPOSITION_ID DESC);

-- LotMixingRelationRepository resolves the inputs of an output LOT.  V014 only
-- indexed the reverse (input LOT) direction.
CREATE INDEX IX_POM_LOT_MIXING_OUTPUT
    ON POM_LOT_MIXING_RELATION (PLANT_ID, OUTPUT_LOT_ID, INPUT_LOT_ID);

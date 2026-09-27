-- Supports bounded parent-to-child split traversal in durable creation order.
CREATE INDEX IX_IVT_SPLIT_PARENT_CREATED
    ON IVT_MATERIAL_LOT_SPLIT (PARENT_LOT_ID, CREATED_AT, SPLIT_ID);

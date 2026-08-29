-- Owner: IVT. Material-consumption accounting evidence is immutable; reversals are new rows.
-- Existing UX_IVT_MATERIAL_CONSUMPTION_KEY and reversal/source indexes reject insert collisions.

-- SQLITE-OMIT-BEGIN
EXEC(N'CREATE TRIGGER TR_IVT_MATERIAL_CONSUMPTION_APPEND_ONLY
ON IVT_MATERIAL_CONSUMPTION_HISTORY
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51237, ''IVT_MATERIAL_CONSUMPTION_HISTORY is append-only'', 1;
END');
-- SQLITE-OMIT-END

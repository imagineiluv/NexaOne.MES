-- Owner: EMS. One physical equipment position can hold at most one active production tool.
-- NULL means that no physical position was identified and remains outside this uniqueness rule.
-- ExecuteNonQuery does not expose a preceding result set reliably to the migration runner, so put
-- the representative conflict directly in THROW. The runner then reports actionable evidence.
-- SQLITE-OMIT-BEGIN
DECLARE @ConflictEquipmentId NVARCHAR(50);
DECLARE @ConflictToolPosition NVARCHAR(100);
DECLARE @ConflictCount BIGINT;
DECLARE @FirstMountId NVARCHAR(50);

SELECT TOP (1)
       @ConflictEquipmentId = EQUIPMENT_ID,
       @ConflictToolPosition = POSITION_CODE,
       @ConflictCount = COUNT_BIG(*),
       @FirstMountId = MIN(MOUNT_ID)
FROM EMS_TOOL_MOUNT_HISTORY
WHERE UNMOUNTED_AT IS NULL AND POSITION_CODE IS NOT NULL
GROUP BY EQUIPMENT_ID, POSITION_CODE
HAVING COUNT_BIG(*) > 1
ORDER BY COUNT_BIG(*) DESC, EQUIPMENT_ID, POSITION_CODE;

IF @ConflictCount IS NOT NULL
BEGIN
    DECLARE @ConflictMessage NVARCHAR(2048) = CONCAT(
        N'V121 cannot create UX_EMS_TOOL_ACTIVE_EQUIPMENT_POSITION. ',
        N'EQUIPMENT_ID=''', @ConflictEquipmentId,
        N''', TOOL_POSITION=''', @ConflictToolPosition,
        N''', ACTIVE_COUNT=', @ConflictCount,
        N', FIRST_MOUNT_ID=''', @FirstMountId,
        N'''. Reconcile the physical mount state first.');

    THROW 51221, @ConflictMessage, 1;
END;

CREATE UNIQUE INDEX UX_EMS_TOOL_ACTIVE_EQUIPMENT_POSITION
    ON EMS_TOOL_MOUNT_HISTORY (EQUIPMENT_ID, POSITION_CODE)
    WHERE UNMOUNTED_AT IS NULL AND POSITION_CODE IS NOT NULL;
-- SQLITE-OMIT-END

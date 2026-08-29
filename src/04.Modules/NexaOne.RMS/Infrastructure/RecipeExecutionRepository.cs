using System.Data;
using System.Data.Common;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.RMS.Application.Rms;
using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.RMS.Infrastructure;

public sealed class RecipeExecutionRepository : QueryRepository, IRecipeExecutionRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly ITransactionManager _transactionManager;
    private readonly DatabaseEndpoint _endpoint;

    public RecipeExecutionRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _transactionManager = dataSource.Provider.TransactionManager;
        _endpoint = new DatabaseEndpoint(
            "NexaOneEES", dataSource.Provider.Kind, dataSource.ConnectionString);
    }

    public Task<bool> TrySaveReleasedAssignmentAsync(
        RecipeEquipmentAssignment assignment, CancellationToken ct = default)
    {
        var p = new
        {
            assignment.AssignmentId,
            assignment.EquipmentId,
            assignment.EquipmentClassId,
            assignment.RecipeId,
            assignment.RecipeVersion,
            assignment.EffectiveFrom,
            assignment.EffectiveTo,
            assignment.AssignedBy,
            assignment.IsActive,
            Now = DateTime.UtcNow,
        };
        return _transactionManager.ExecuteInTransactionAsync(
            _endpoint,
            async (connection, transaction) =>
            {
                var inserted = await connection.ExecuteAsync(new CommandDefinition(
                    InsertReleasedAssignmentSql, p, transaction, cancellationToken: ct))
                    .ConfigureAwait(false);
                if (inserted == 0) return false;
                if (inserted != 1)
                    throw new DBConcurrencyException(
                        $"Recipe assignment guard inserted {inserted} rows; expected exactly one.");

                await connection.ExecuteAsync(new CommandDefinition(
                    DeactivateAssignmentSql, p, transaction, cancellationToken: ct))
                    .ConfigureAwait(false);
                var finalized = await connection.ExecuteAsync(new CommandDefinition(
                    FinalizeAssignmentSql, p, transaction, cancellationToken: ct))
                    .ConfigureAwait(false);
                if (finalized != 1)
                    throw new DBConcurrencyException(
                        $"Recipe assignment finalize affected {finalized} rows; expected exactly one.");
                return true;
            },
            IsolationLevel.Serializable,
            ct);
    }

    public async Task<IReadOnlyList<RecipeEquipmentAssignment>> GetAssignmentsAsync(
        string? equipmentId,
        string? equipmentClassId,
        bool activeOnly,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM RMS_RECIPE_EQUIPMENT_ASSIGNMENT
            WHERE (@equipmentId IS NULL OR EQUIPMENT_ID = @equipmentId)
              AND (@equipmentClassId IS NULL OR EQUIPMENT_CLASS_ID = @equipmentClassId)
              AND (@activeOnly = 0 OR IS_ACTIVE = 1)
            ORDER BY EFFECTIVE_FROM DESC, ASSIGNMENT_ID";
        var rows = await QueryAsync<AssignmentRow>(sql, new
        {
            equipmentId,
            equipmentClassId,
            activeOnly,
        }, ct);
        return rows.Select(row => row.ToRecord()).ToList();
    }

    public async Task<RecipeEquipmentAssignment?> GetEffectiveAssignmentAsync(
        string equipmentId,
        string equipmentClassId,
        DateTime appliedAt,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM RMS_RECIPE_EQUIPMENT_ASSIGNMENT
            WHERE EFFECTIVE_FROM <= @appliedAt
              AND (EFFECTIVE_TO IS NULL OR EFFECTIVE_TO > @appliedAt)
              AND (EQUIPMENT_ID = @equipmentId
                OR (EQUIPMENT_ID IS NULL AND EQUIPMENT_CLASS_ID = @equipmentClassId))
            ORDER BY CASE WHEN EQUIPMENT_ID IS NOT NULL THEN 0 ELSE 1 END,
                     EFFECTIVE_FROM DESC, ASSIGNMENT_ID";
        var rows = await QueryAsync<AssignmentRow>(sql, new
        {
            equipmentId,
            equipmentClassId,
            appliedAt,
        }, ct);
        return rows.Select(row => row.ToRecord()).FirstOrDefault();
    }

    public async Task<RecipeExecutionSnapshot?> GetExecutionAsync(
        string executionId, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<ExecutionRow>(
            ExecutionSelect + " WHERE EXECUTION_ID = @executionId",
            new { executionId }, ct))?.ToRecord();

    public async Task<RecipeExecutionSnapshot?> GetExecutionByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<ExecutionRow>(
            ExecutionSelect + " WHERE IDEMPOTENCY_KEY = @idempotencyKey",
            new { idempotencyKey }, ct))?.ToRecord();

    public Task<bool> TryAddExecutionAsync(
        RecipeExecutionSnapshot snapshot,
        CancellationToken ct = default)
        => Task.FromResult(false);

    public async Task<bool> TryAddAssignedExecutionAsync(
        RecipeExecutionSnapshot snapshot,
        string assignmentId,
        string equipmentClassId,
        CancellationToken ct = default)
    {
        var parameters = new
        {
            snapshot.ExecutionId,
            snapshot.IdempotencyKey,
            snapshot.RequestHash,
            snapshot.PlantId,
            snapshot.EquipmentId,
            snapshot.ProcessLotId,
            snapshot.WorkOrderId,
            snapshot.ProcessId,
            snapshot.WorkScopeId,
            snapshot.CarrierId,
            snapshot.RecipeId,
            snapshot.RecipeVersion,
            snapshot.RecipeSnapshotJson,
            snapshot.ParameterSnapshotJson,
            snapshot.ConditionSnapshotJson,
            snapshot.AppliedBy,
            snapshot.AppliedAt,
            snapshot.Source,
            snapshot.TraceId,
            snapshot.CreatedAt,
            AssignmentId = assignmentId,
            EquipmentClassId = equipmentClassId,
        };
        try
        {
            // Released Recipe와 적용기간 내 assignment를 같은 INSERT ... SELECT 문에서 검사한다.
            // 서비스의 사전 조회 뒤 assignment가 교체되어도 이 원자 guard가 우회를 차단한다.
            return await _processor.ExecuteGuardedManyAsync(ct, (InsertAssignedExecutionSql, parameters));
        }
        catch (DbException)
        {
            if (await GetExecutionByIdempotencyKeyAsync(snapshot.IdempotencyKey, ct) is not null)
                return false;
            throw;
        }
    }

    private const string DeactivateAssignmentSql = @"UPDATE RMS_RECIPE_EQUIPMENT_ASSIGNMENT SET
        IS_ACTIVE = 0, EFFECTIVE_TO = @EffectiveFrom,
        UPDATED_BY = @AssignedBy, UPDATED_AT = @Now
        WHERE @IsActive = 1 AND IS_ACTIVE = 1
          AND ASSIGNMENT_ID <> @AssignmentId
          AND EFFECTIVE_FROM < @EffectiveFrom
          AND ((@EquipmentId IS NOT NULL AND EQUIPMENT_ID = @EquipmentId)
            OR (@EquipmentClassId IS NOT NULL AND EQUIPMENT_CLASS_ID = @EquipmentClassId))";

    private const string InsertReleasedAssignmentSql = @"INSERT INTO RMS_RECIPE_EQUIPMENT_ASSIGNMENT
        (ASSIGNMENT_ID, EQUIPMENT_ID, EQUIPMENT_CLASS_ID, RECIPE_ID, RECIPE_VERSION,
         EFFECTIVE_FROM, EFFECTIVE_TO, ASSIGNED_BY, IS_ACTIVE,
         CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        SELECT
         @AssignmentId, @EquipmentId, @EquipmentClassId, @RecipeId, @RecipeVersion,
         @EffectiveFrom, @EffectiveTo, @AssignedBy, 0,
         @AssignedBy, @Now, @AssignedBy, @Now
        FROM RMS_RECIPE
        WHERE RECIPE_ID = @RecipeId
          AND VERSION = @RecipeVersion
          AND APPROVAL_STATE = 'Released'
          AND @EffectiveFrom <= @Now
          AND NOT EXISTS (
              SELECT 1 FROM RMS_RECIPE_EQUIPMENT_ASSIGNMENT A
              WHERE A.IS_ACTIVE = 1
                AND A.EFFECTIVE_FROM >= @EffectiveFrom
                AND ((@EquipmentId IS NOT NULL AND A.EQUIPMENT_ID = @EquipmentId)
                  OR (@EquipmentClassId IS NOT NULL AND A.EQUIPMENT_CLASS_ID = @EquipmentClassId))
          )";

    private const string FinalizeAssignmentSql = @"UPDATE RMS_RECIPE_EQUIPMENT_ASSIGNMENT SET
        IS_ACTIVE = @IsActive, UPDATED_BY = @AssignedBy, UPDATED_AT = @Now
        WHERE ASSIGNMENT_ID = @AssignmentId";

    private const string ExecutionSelect = @"SELECT
        EXECUTION_ID AS ExecutionId,
        IDEMPOTENCY_KEY AS IdempotencyKey,
        REQUEST_HASH AS RequestHash,
        PLANT_ID AS PlantId,
        EQUIPMENT_ID AS EquipmentId,
        PROCESS_LOT_ID AS ProcessLotId,
        WORK_ORDER_ID AS WorkOrderId,
        PROCESS_ID AS ProcessId,
        WORK_SCOPE_ID AS WorkScopeId,
        CARRIER_ID AS CarrierId,
        RECIPE_ID AS RecipeId,
        RECIPE_VERSION AS RecipeVersion,
        RECIPE_SNAPSHOT_JSON AS RecipeSnapshotJson,
        PARAMETER_SNAPSHOT_JSON AS ParameterSnapshotJson,
        CONDITION_SNAPSHOT_JSON AS ConditionSnapshotJson,
        APPLIED_BY AS AppliedBy,
        APPLIED_AT AS AppliedAt,
        SOURCE AS Source,
        TRACE_ID AS TraceId,
        CREATED_AT AS CreatedAt
        FROM RMS_RECIPE_EXECUTION_SNAPSHOT";

    private const string InsertAssignedExecutionSql = @"INSERT INTO RMS_RECIPE_EXECUTION_SNAPSHOT
        (EXECUTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
         PROCESS_LOT_ID, WORK_ORDER_ID, PROCESS_ID, WORK_SCOPE_ID, CARRIER_ID,
         RECIPE_ID, RECIPE_VERSION,
         RECIPE_SNAPSHOT_JSON, PARAMETER_SNAPSHOT_JSON, CONDITION_SNAPSHOT_JSON,
         APPLIED_BY, APPLIED_AT, SOURCE, TRACE_ID, CREATED_AT)
        SELECT @ExecutionId, @IdempotencyKey, @RequestHash, @PlantId, @EquipmentId,
               @ProcessLotId, @WorkOrderId, @ProcessId, @WorkScopeId, @CarrierId,
               @RecipeId, @RecipeVersion,
               @RecipeSnapshotJson, @ParameterSnapshotJson, @ConditionSnapshotJson,
               @AppliedBy, @AppliedAt, @Source, @TraceId, @CreatedAt
        FROM RMS_RECIPE R
        INNER JOIN RMS_RECIPE_EQUIPMENT_ASSIGNMENT A
            ON A.ASSIGNMENT_ID = @AssignmentId
        WHERE R.RECIPE_ID = @RecipeId
           AND R.VERSION = @RecipeVersion
           AND R.APPROVAL_STATE = 'Released'
          AND R.EQUIPMENT_CLASS_ID = @EquipmentClassId
           AND A.RECIPE_ID = @RecipeId
           AND A.RECIPE_VERSION = @RecipeVersion
          AND A.EFFECTIVE_FROM <= @AppliedAt
          AND (A.EFFECTIVE_TO IS NULL OR A.EFFECTIVE_TO > @AppliedAt)
          AND (A.EQUIPMENT_ID = @EquipmentId
            OR (A.EQUIPMENT_ID IS NULL AND A.EQUIPMENT_CLASS_ID = @EquipmentClassId))
          AND NOT EXISTS (
              SELECT 1 FROM RMS_RECIPE_EQUIPMENT_ASSIGNMENT W
              WHERE W.EFFECTIVE_FROM <= @AppliedAt
                AND (W.EFFECTIVE_TO IS NULL OR W.EFFECTIVE_TO > @AppliedAt)
                AND (W.EQUIPMENT_ID = @EquipmentId
                  OR (W.EQUIPMENT_ID IS NULL AND W.EQUIPMENT_CLASS_ID = @EquipmentClassId))
                AND ((A.EQUIPMENT_ID IS NULL AND W.EQUIPMENT_ID IS NOT NULL)
                  OR (((A.EQUIPMENT_ID IS NULL AND W.EQUIPMENT_ID IS NULL)
                       OR (A.EQUIPMENT_ID IS NOT NULL AND W.EQUIPMENT_ID IS NOT NULL))
                    AND (W.EFFECTIVE_FROM > A.EFFECTIVE_FROM
                      OR (W.EFFECTIVE_FROM = A.EFFECTIVE_FROM
                        AND W.ASSIGNMENT_ID < A.ASSIGNMENT_ID)))))
           AND NOT EXISTS (
              SELECT 1 FROM RMS_RECIPE_EXECUTION_SNAPSHOT
              WHERE IDEMPOTENCY_KEY = @IdempotencyKey)";

    private sealed class AssignmentRow
    {
        public string AssignmentId { get; set; } = "";
        public string? EquipmentId { get; set; }
        public string? EquipmentClassId { get; set; }
        public string RecipeId { get; set; } = "";
        public int RecipeVersion { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public string AssignedBy { get; set; } = "";
        public bool IsActive { get; set; }

        public RecipeEquipmentAssignment ToRecord() => new(
            AssignmentId, EquipmentId, EquipmentClassId, RecipeId, RecipeVersion,
            EffectiveFrom, EffectiveTo, AssignedBy, IsActive);
    }

    private sealed class ExecutionRow
    {
        public string ExecutionId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string PlantId { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string? ProcessLotId { get; set; }
        public string? WorkOrderId { get; set; }
        public string? ProcessId { get; set; }
        public string? WorkScopeId { get; set; }
        public string? CarrierId { get; set; }
        public string RecipeId { get; set; } = "";
        public int RecipeVersion { get; set; }
        public string RecipeSnapshotJson { get; set; } = "";
        public string ParameterSnapshotJson { get; set; } = "";
        public string? ConditionSnapshotJson { get; set; }
        public string AppliedBy { get; set; } = "";
        public DateTime AppliedAt { get; set; }
        public string Source { get; set; } = "";
        public string? TraceId { get; set; }
        public DateTime CreatedAt { get; set; }

        public RecipeExecutionSnapshot ToRecord() => new(
            ExecutionId, IdempotencyKey, RequestHash, PlantId, EquipmentId,
            ProcessLotId, WorkOrderId, ProcessId, RecipeId, RecipeVersion,
            RecipeSnapshotJson, ParameterSnapshotJson, ConditionSnapshotJson,
            AppliedBy, AppliedAt, Source, TraceId, CreatedAt,
            false, WorkScopeId, CarrierId);
    }
}

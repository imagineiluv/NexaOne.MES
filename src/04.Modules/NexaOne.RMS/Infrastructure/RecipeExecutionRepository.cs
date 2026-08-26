using System.Data;
using System.Data.Common;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.RMS.Application.Rms;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;

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

    public async Task<bool> TryAddExecutionAsync(
        RecipeExecutionSnapshot snapshot, CancellationToken ct = default)
    {
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct, (InsertExecutionSql, snapshot));
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
          AND APPROVAL_STATE = 'Released'";

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

    private const string InsertExecutionSql = @"INSERT INTO RMS_RECIPE_EXECUTION_SNAPSHOT
        (EXECUTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
         PROCESS_LOT_ID, WORK_ORDER_ID, PROCESS_ID, RECIPE_ID, RECIPE_VERSION,
         RECIPE_SNAPSHOT_JSON, PARAMETER_SNAPSHOT_JSON, CONDITION_SNAPSHOT_JSON,
         APPLIED_BY, APPLIED_AT, SOURCE, TRACE_ID, CREATED_AT)
        SELECT @ExecutionId, @IdempotencyKey, @RequestHash, @PlantId, @EquipmentId,
               @ProcessLotId, @WorkOrderId, @ProcessId, @RecipeId, @RecipeVersion,
               @RecipeSnapshotJson, @ParameterSnapshotJson, @ConditionSnapshotJson,
               @AppliedBy, @AppliedAt, @Source, @TraceId, @CreatedAt
        FROM RMS_RECIPE
        WHERE RECIPE_ID = @RecipeId
          AND VERSION = @RecipeVersion
          AND APPROVAL_STATE = 'Released'
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
            AppliedBy, AppliedAt, Source, TraceId, CreatedAt);
    }
}

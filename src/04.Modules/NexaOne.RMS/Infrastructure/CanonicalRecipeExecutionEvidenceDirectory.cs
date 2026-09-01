using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Rms;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.RMS.Infrastructure;

public sealed class CanonicalRecipeExecutionEvidenceDirectory
    : QueryRepository, ICanonicalRecipeExecutionEvidenceDirectory
{
    private readonly bool _isSqlServer;

    public CanonicalRecipeExecutionEvidenceDirectory(EesDataSource dataSource) : base(dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _isSqlServer = dataSource.Provider.Kind == DatabaseProviderKind.SqlServer;
    }

    public async Task<CanonicalRecipeExecutionEvidenceDto?> FindAsync(string executionId, CancellationToken ct = default)
    {
        var sql = _isSqlServer ? SelectExactSqlServer : SelectExactSqlite;
        var row = await QueryFirstOrDefaultAsync<Row>(sql, new { executionId }, ct);
        return row?.ToDto();
    }

    private const string SelectColumns = @"SELECT EXECUTION_ID, WORK_SCOPE_ID, PAIR_RUN_ID, SEQUENCE_RUN_ID,
            EQUIPMENT_ID, OPERATION_KEY, RECIPE_ID, RECIPE_VERSION, SNAPSHOT_SCHEMA, SNAPSHOT_HASH, CAPTURED_AT
            FROM RMS_CANONICAL_RECIPE_EXECUTION_EVIDENCE";

    private const string SelectExactSqlServer = SelectColumns + @"
            WHERE EXECUTION_ID COLLATE Latin1_General_100_BIN2
                  = @executionId COLLATE Latin1_General_100_BIN2
              AND DATALENGTH(CONVERT(NVARCHAR(MAX), EXECUTION_ID))
                  = DATALENGTH(CONVERT(NVARCHAR(MAX), @executionId))";

    private const string SelectExactSqlite = SelectColumns + @"
            WHERE EXECUTION_ID COLLATE BINARY = @executionId COLLATE BINARY";

    private sealed class Row
    {
        public string ExecutionId { get; set; } = ""; public string WorkScopeId { get; set; } = "";
        public string PairRunId { get; set; } = ""; public string SequenceRunId { get; set; } = "";
        public string EquipmentId { get; set; } = ""; public string OperationKey { get; set; } = "";
        public string RecipeId { get; set; } = ""; public int RecipeVersion { get; set; }
        public string SnapshotSchema { get; set; } = ""; public string SnapshotHash { get; set; } = "";
        public DateTime CapturedAt { get; set; }
        public CanonicalRecipeExecutionEvidenceDto ToDto() => new(ExecutionId, WorkScopeId, PairRunId,
            SequenceRunId, EquipmentId, OperationKey, RecipeId, RecipeVersion, SnapshotSchema,
            SnapshotHash, CapturedAt);
    }
}

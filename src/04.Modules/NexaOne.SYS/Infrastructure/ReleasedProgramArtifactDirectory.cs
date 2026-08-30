using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Sys;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.SYS.Infrastructure;

public sealed class ReleasedProgramArtifactDirectory
    : QueryRepository, IReleasedProgramArtifactDirectory
{
    private readonly bool _isSqlServer;

    public ReleasedProgramArtifactDirectory(EesDataSource dataSource) : base(dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _isSqlServer = dataSource.Provider.Kind == DatabaseProviderKind.SqlServer;
    }

    public async Task<ReleasedProgramArtifactDto?> FindAsync(string artifactId, CancellationToken ct = default)
    {
        var sql = _isSqlServer ? SelectExactSqlServer : SelectExactSqlite;
        var row = await QueryFirstOrDefaultAsync<Row>(sql, new { artifactId }, ct);
        return row?.ToDto();
    }

    private const string SelectColumns = @"SELECT A.ARTIFACT_ID, A.EQUIPMENT_ID, A.OPERATION_KEY, A.PRODUCT_PROFILE_ID,
            A.PLUGIN_ID, A.PRODUCT_DEFINITION_VERSION, A.PROGRAM_VERSION, A.PROGRAM_SCHEMA, A.PROGRAM_HASH,
            A.BOUND_RECIPE_SNAPSHOT_SCHEMA, A.BOUND_RECIPE_SNAPSHOT_HASH, A.RELEASED_AT, A.RELEASED_BY,
            R.REVOCATION_ID, R.REVOKED_AT, R.REVOKED_BY, R.REASON
            FROM SYS_RELEASED_PROGRAM_ARTIFACT A";

    private const string SelectExactSqlServer = SelectColumns + @"
            LEFT JOIN SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION R
              ON R.ARTIFACT_ID COLLATE Latin1_General_100_BIN2
                   = A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2
             AND DATALENGTH(CONVERT(NVARCHAR(MAX), R.ARTIFACT_ID))
                   = DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID))
            WHERE A.ARTIFACT_ID COLLATE Latin1_General_100_BIN2
                  = @artifactId COLLATE Latin1_General_100_BIN2
              AND DATALENGTH(CONVERT(NVARCHAR(MAX), A.ARTIFACT_ID))
                  = DATALENGTH(CONVERT(NVARCHAR(MAX), @artifactId))";

    private const string SelectExactSqlite = SelectColumns + @"
            LEFT JOIN SYS_RELEASED_PROGRAM_ARTIFACT_REVOCATION R
              ON R.ARTIFACT_ID COLLATE BINARY = A.ARTIFACT_ID COLLATE BINARY
            WHERE A.ARTIFACT_ID COLLATE BINARY = @artifactId COLLATE BINARY";

    private sealed class Row
    {
        public string ArtifactId { get; set; } = ""; public string EquipmentId { get; set; } = "";
        public string OperationKey { get; set; } = ""; public string ProductProfileId { get; set; } = "";
        public string PluginId { get; set; } = ""; public string ProductDefinitionVersion { get; set; } = "";
        public string ProgramVersion { get; set; } = ""; public string ProgramSchema { get; set; } = "";
        public string ProgramHash { get; set; } = ""; public string BoundRecipeSnapshotSchema { get; set; } = "";
        public string BoundRecipeSnapshotHash { get; set; } = ""; public DateTime ReleasedAt { get; set; }
        public string ReleasedBy { get; set; } = ""; public string? RevocationId { get; set; }
        public DateTime? RevokedAt { get; set; } public string? RevokedBy { get; set; } public string? Reason { get; set; }
        public ReleasedProgramArtifactDto ToDto() => new(ArtifactId, EquipmentId, OperationKey, ProductProfileId,
            PluginId, ProductDefinitionVersion, ProgramVersion, ProgramSchema, ProgramHash,
            BoundRecipeSnapshotSchema, BoundRecipeSnapshotHash, ReleasedAt, ReleasedBy,
            RevocationId is null ? null : new(RevocationId, ArtifactId, RevokedAt!.Value, RevokedBy!, Reason!));
    }
}

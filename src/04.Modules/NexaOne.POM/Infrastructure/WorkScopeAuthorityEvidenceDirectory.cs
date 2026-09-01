using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Pom;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.POM.Infrastructure;

public sealed class WorkScopeAuthorityEvidenceDirectory
    : QueryRepository, IWorkScopeAuthorityEvidenceDirectory
{
    private readonly bool _isSqlServer;

    public WorkScopeAuthorityEvidenceDirectory(EesDataSource dataSource) : base(dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _isSqlServer = dataSource.Provider.Kind == DatabaseProviderKind.SqlServer;
    }

    public async Task<WorkScopeDto?> FindAsync(string workScopeId, CancellationToken ct = default)
    {
        var sql = _isSqlServer ? SelectExactSqlServer : SelectExactSqlite;
        var row = await QueryFirstOrDefaultAsync<Row>(sql, new { workScopeId }, ct);
        return row?.ToDto();
    }

    private const string SelectExactSqlServer = """
        SELECT * FROM POM_WORK_SCOPE
         WHERE WORK_SCOPE_ID COLLATE Latin1_General_100_BIN2
             = @workScopeId COLLATE Latin1_General_100_BIN2
           AND DATALENGTH(CONVERT(NVARCHAR(MAX), WORK_SCOPE_ID))
             = DATALENGTH(CONVERT(NVARCHAR(MAX), @workScopeId))
        """;

    private const string SelectExactSqlite = """
        SELECT * FROM POM_WORK_SCOPE
         WHERE WORK_SCOPE_ID COLLATE BINARY = @workScopeId COLLATE BINARY
        """;

    private sealed class Row
    {
        public string WorkScopeId { get; set; } = ""; public string PlantId { get; set; } = "";
        public string ScopeType { get; set; } = ""; public string TargetId { get; set; } = ""; public string Name { get; set; } = "";
        public string? ParentScopeId { get; set; } public string? EquipmentId { get; set; } public string? ProductId { get; set; }
        public string? ProcessId { get; set; } public string? RecipeId { get; set; } public int? RecipeVersion { get; set; }
        public decimal? PlanQty { get; set; } public decimal StartQty { get; set; } public decimal CompleteQty { get; set; }
        public decimal ScrapQty { get; set; } public string? OwnerId { get; set; } public string Status { get; set; } = "";
        public string IsHold { get; set; } = "N"; public DateTime? StartedAt { get; set; } public DateTime? CompletedAt { get; set; }
        public string? Description { get; set; } public int VersionNo { get; set; } public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; } = ""; public DateTime? UpdatedAt { get; set; } public string? UpdatedBy { get; set; }
        public string? WorkOrderId { get; set; } public string? CarrierId { get; set; }
        public WorkScopeDto ToDto() => new(WorkScopeId, PlantId, ScopeType, TargetId, Name, ParentScopeId,
            EquipmentId, ProductId, ProcessId, RecipeId, RecipeVersion, PlanQty, StartQty, CompleteQty,
            ScrapQty, OwnerId, Status, string.Equals(IsHold, "Y", StringComparison.Ordinal), StartedAt, CompletedAt, Description, VersionNo, CreatedAt,
            CreatedBy, UpdatedAt, UpdatedBy, WorkOrderId, CarrierId);
    }
}

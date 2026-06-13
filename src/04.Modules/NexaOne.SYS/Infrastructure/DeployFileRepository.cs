using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Deploys;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Infrastructure;

public sealed class DeployFileRepository : QueryRepository, IDeployFileRepository
{
    private readonly ServiceObjectProcessor _processor;

    public DeployFileRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<IReadOnlyList<DeployFile>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SYS_DEPLOY_FILE
            ORDER BY UPLOADED_AT DESC";
        var rows = await QueryAsync<DeployRow>(sql, null, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<DeployFile>> GetActiveAsync(CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SYS_DEPLOY_FILE
            WHERE IS_ACTIVE = 1
            ORDER BY UPLOADED_AT DESC";
        var rows = await QueryAsync<DeployRow>(sql, null, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<DeployFile?> GetByIdAsync(string fileId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SYS_DEPLOY_FILE
            WHERE FILE_ID = @fileId";
        var row = await QueryFirstOrDefaultAsync<DeployRow>(sql, new { fileId }, ct);
        return row?.ToDomain();
    }

    public async Task<DeployFile?> GetByVersionAsync(string version, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SYS_DEPLOY_FILE
            WHERE VERSION = @version";
        var row = await QueryFirstOrDefaultAsync<DeployRow>(sql, new { version }, ct);
        return row?.ToDomain();
    }

    public async Task InsertAsync(DeployFile file, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO SYS_DEPLOY_FILE
            (FILE_ID, VERSION, FILE_NAME, HASH, FILE_SIZE, DESCRIPTION,
             FORCE_UPDATE, IS_ACTIVE, UPLOADED_BY, UPLOADED_AT)
            VALUES
            (@FileId, @Version, @FileName, @Hash, @FileSize, @Description,
             @ForceUpdate, @IsActive, @UploadedBy, @UploadedAt)";
        await _processor.InsertAsync(sql, DeployRow.FromDomain(file), ct);
    }

    public async Task UpdateAsync(DeployFile file, CancellationToken ct = default)
    {
        // FILE_ID/VERSION/HASH 등 식별·무결성 필드는 불변 — 가변 필드만 갱신한다
        const string sql = @"UPDATE SYS_DEPLOY_FILE
            SET DESCRIPTION = @Description, FORCE_UPDATE = @ForceUpdate, IS_ACTIVE = @IsActive
            WHERE FILE_ID = @FileId";
        await _processor.UpdateAsync(sql, DeployRow.FromDomain(file), ct);
    }

    private sealed class DeployRow
    {
        public string FileId { get; set; } = "";
        public string Version { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Hash { get; set; } = "";
        public long FileSize { get; set; }
        public string Description { get; set; } = "";
        public bool ForceUpdate { get; set; }
        public bool IsActive { get; set; }
        public string UploadedBy { get; set; } = "";
        public DateTime UploadedAt { get; set; }

        public DeployFile ToDomain() => DeployFile.Restore(
            FileId, Version, FileName, Hash, FileSize, Description,
            ForceUpdate, IsActive, UploadedBy, UploadedAt);

        public static DeployRow FromDomain(DeployFile f) => new()
        {
            FileId = f.FileId,
            Version = f.Version,
            FileName = f.FileName,
            Hash = f.Hash,
            FileSize = f.FileSize,
            Description = f.Description,
            ForceUpdate = f.ForceUpdate,
            IsActive = f.IsActive,
            UploadedBy = f.UploadedBy,
            UploadedAt = f.UploadedAt
        };
    }
}

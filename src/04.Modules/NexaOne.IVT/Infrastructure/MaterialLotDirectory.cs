using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Infrastructure;

/// <summary>IVT 자재 LOT를 축소 snapshot으로 제공하는 owner adapter입니다.</summary>
public sealed class MaterialLotDirectory : QueryRepository, IMaterialLotDirectory
{
    public MaterialLotDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<MaterialLotDirectoryEntry?> GetLotAsync(
        string lotId,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT LOT_ID AS LotId, MATERIAL_ID AS MaterialId
                             FROM IVT_MATERIAL_LOT
                             WHERE LOT_ID = @lotId";
        return await QueryFirstOrDefaultAsync<MaterialLotDirectoryEntry>(
            sql,
            new { lotId },
            ct);
    }
}

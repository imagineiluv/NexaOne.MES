using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.FDC.Infrastructure;

public sealed class FdcParameterGroupRepository : QueryRepository, IFdcParameterGroupRepository
{
    private readonly ServiceObjectProcessor _processor;

    public FdcParameterGroupRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<FdcParameterGroup?> GetByIdAsync(string groupId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM FDC_PARAMETER_GROUP WITH(NOLOCK) WHERE GROUP_ID = @groupId";
        var row = await QueryFirstOrDefaultAsync<GroupRow>(sql, new { groupId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<FdcParameterGroup>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_PARAMETER_GROUP WITH(NOLOCK)
            WHERE EQUIPMENT_ID = @equipmentId
            ORDER BY DISPLAY_ORDER, GROUP_NAME";
        var rows = await QueryAsync<GroupRow>(sql, new { equipmentId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcParameterGroup>().ToList();
    }

    public async Task AddAsync(FdcParameterGroup group, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO FDC_PARAMETER_GROUP
            (GROUP_ID, GROUP_NAME, EQUIPMENT_ID, DESCRIPTION, DISPLAY_ORDER, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@GroupId, @GroupName, @EquipmentId, @Description, @DisplayOrder, @IsActive,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, GroupRow.FromDomain(group), ct);
    }

    public async Task UpdateAsync(FdcParameterGroup group, CancellationToken ct = default)
    {
        const string sql = @"UPDATE FDC_PARAMETER_GROUP SET
            GROUP_NAME = @GroupName, DESCRIPTION = @Description, DISPLAY_ORDER = @DisplayOrder, IS_ACTIVE = @IsActive,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE GROUP_ID = @GroupId";
        await _processor.UpdateAsync(sql, GroupRow.FromDomain(group), ct);
    }

    private sealed class GroupRow
    {
        public string  GroupId      { get; set; } = "";
        public string  GroupName    { get; set; } = "";
        public string  EquipmentId  { get; set; } = "";
        public string? Description  { get; set; }
        public int     DisplayOrder { get; set; }
        public bool    IsActive     { get; set; }

        public FdcParameterGroup? ToDomain()
        {
            var result = FdcParameterGroup.Create(GroupId, GroupName, EquipmentId, Description, DisplayOrder);
            if (result.IsFailure) return null;
            var g = result.Value;
            if (!IsActive) g.Deactivate();
            return g;
        }

        public static GroupRow FromDomain(FdcParameterGroup g) => new()
        {
            GroupId      = g.Id,
            GroupName    = g.GroupName,
            EquipmentId  = g.EquipmentId,
            Description  = g.Description,
            DisplayOrder = g.DisplayOrder,
            IsActive     = g.IsActive
        };
    }
}

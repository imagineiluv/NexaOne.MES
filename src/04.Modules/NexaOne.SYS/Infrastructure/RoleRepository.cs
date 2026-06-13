using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Infrastructure;

public sealed class RoleRepository : QueryRepository, IRoleRepository
{
    private readonly ServiceObjectProcessor _processor;

    public RoleRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<Role?> GetByIdAsync(string roleId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SYS_ROLE WHERE ROLE_ID = @roleId AND IS_DELETED = 0";
        var row = await QueryFirstOrDefaultAsync<RoleRow>(sql, new { roleId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<Role>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SYS_ROLE WHERE IS_DELETED = 0 ORDER BY ROLE_ID";
        var rows = await QueryAsync<RoleRow>(sql, null, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<bool> ExistsAsync(string roleId, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(1) FROM SYS_ROLE WHERE ROLE_ID = @roleId";
        return await CountAsync(sql, new { roleId }, ct) > 0;
    }

    public async Task AddAsync(Role role, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO SYS_ROLE
            (ROLE_ID, ROLE_NAME, DESCRIPTION, PERMISSIONS, IS_DELETED,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@RoleId, @RoleName, @Description, @Permissions, @IsDeleted,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, RoleRow.FromDomain(role), ct);
    }

    public async Task UpdateAsync(Role role, CancellationToken ct = default)
    {
        const string sql = @"UPDATE SYS_ROLE SET
            ROLE_NAME = @RoleName, DESCRIPTION = @Description,
            PERMISSIONS = @Permissions, IS_DELETED = @IsDeleted,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ROLE_ID = @RoleId";
        await _processor.UpdateAsync(sql, RoleRow.FromDomain(role), ct);
    }

    private sealed class RoleRow
    {
        public string RoleId { get; set; } = "";
        public string RoleName { get; set; } = "";
        public string Description { get; set; } = "";
        public string Permissions { get; set; } = "";
        public bool IsDeleted { get; set; }

        public Role ToDomain()
        {
            var role = Role.Create(RoleId, RoleName, Description);
            if (!string.IsNullOrEmpty(Permissions))
                foreach (var p in Permissions.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    role.AddPermission(p);
            return role;
        }

        public static RoleRow FromDomain(Role r) => new()
        {
            RoleId = r.Id,
            RoleName = r.RoleName,
            Description = r.Description,
            Permissions = string.Join('|', r.Permissions),
            IsDeleted = r.IsDeleted
        };
    }
}

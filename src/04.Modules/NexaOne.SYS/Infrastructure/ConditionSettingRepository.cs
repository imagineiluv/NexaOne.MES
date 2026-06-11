using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Conditions;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Infrastructure;

public sealed class ConditionSettingRepository : QueryRepository, IConditionSettingRepository
{
    private readonly ServiceObjectProcessor _processor;

    public ConditionSettingRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<IReadOnlyList<ConditionSetting>> GetByMenuAsync(
        string userId, string menuId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SYS_CONDITION_SETTING WITH(NOLOCK)
            WHERE USER_ID = @userId AND MENU_ID = @menuId
            ORDER BY SAVED_AT DESC";
        var rows = await QueryAsync<ConditionRow>(sql, new { userId, menuId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<ConditionSetting?> GetAsync(
        string userId, string menuId, string name, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SYS_CONDITION_SETTING WITH(NOLOCK)
            WHERE USER_ID = @userId AND MENU_ID = @menuId AND NAME = @name";
        var row = await QueryFirstOrDefaultAsync<ConditionRow>(sql, new { userId, menuId, name }, ct);
        return row?.ToDomain();
    }

    public async Task UpsertAsync(ConditionSetting setting, CancellationToken ct = default)
    {
        // HOLDLOCK: 동시 업서트 시 MERGE race로 인한 PK 중복 오류 방지 (EquipmentStateRepository와 동일 패턴)
        const string sql = @"MERGE SYS_CONDITION_SETTING WITH (HOLDLOCK) AS t
            USING (SELECT @UserId AS USER_ID, @MenuId AS MENU_ID, @Name AS NAME) AS s
               ON t.USER_ID = s.USER_ID AND t.MENU_ID = s.MENU_ID AND t.NAME = s.NAME
            WHEN MATCHED THEN
                UPDATE SET SAVED_AT = @SavedAt, VALUES_JSON = @ValuesJson
            WHEN NOT MATCHED THEN
                INSERT (USER_ID, MENU_ID, NAME, SAVED_AT, VALUES_JSON)
                VALUES (@UserId, @MenuId, @Name, @SavedAt, @ValuesJson);";
        await _processor.UpdateAsync(sql, ConditionRow.FromDomain(setting), ct);
    }

    public async Task DeleteAsync(
        string userId, string menuId, string name, CancellationToken ct = default)
    {
        const string sql = @"DELETE FROM SYS_CONDITION_SETTING
            WHERE USER_ID = @userId AND MENU_ID = @menuId AND NAME = @name";
        await _processor.DeleteAsync(sql, new { userId, menuId, name }, ct);
    }

    private sealed class ConditionRow
    {
        public string UserId { get; set; } = "";
        public string MenuId { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime SavedAt { get; set; }
        public string ValuesJson { get; set; } = "";

        public ConditionSetting ToDomain() =>
            ConditionSetting.Create(UserId, MenuId, Name, ValuesJson, SavedAt);

        public static ConditionRow FromDomain(ConditionSetting s) => new()
        {
            UserId = s.UserId,
            MenuId = s.MenuId,
            Name = s.Name,
            SavedAt = s.SavedAt,
            ValuesJson = s.ValuesJson
        };
    }
}

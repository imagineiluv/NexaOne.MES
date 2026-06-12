using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Infrastructure;

public sealed class MultiLanguageResourceRepository : QueryRepository, IMultiLanguageResourceRepository
{
    private readonly ServiceObjectProcessor _processor;

    public MultiLanguageResourceRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<MultiLanguageResource?> GetByIdAsync(string resourceKey, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SYS_MULTI_LANGUAGE_RESOURCE WITH(NOLOCK) WHERE RESOURCE_KEY = @resourceKey";
        var row = await QueryFirstOrDefaultAsync<LangRow>(sql, new { resourceKey }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<MultiLanguageResource>> GetByMenuIdAsync(string menuId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SYS_MULTI_LANGUAGE_RESOURCE WITH(NOLOCK) WHERE MENU_ID = @menuId ORDER BY LANGUAGE";
        var rows = await QueryAsync<LangRow>(sql, new { menuId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<MultiLanguageResource>> GetByLanguageAsync(LanguageType language, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SYS_MULTI_LANGUAGE_RESOURCE WITH(NOLOCK) WHERE LANGUAGE = @language ORDER BY MENU_ID";
        var rows = await QueryAsync<LangRow>(sql, new { language = language.ToString() }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<bool> ExistsAsync(string resourceKey, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(1) FROM SYS_MULTI_LANGUAGE_RESOURCE WITH(NOLOCK) WHERE RESOURCE_KEY = @resourceKey";
        return await CountAsync(sql, new { resourceKey }, ct) > 0;
    }

    public async Task AddAsync(MultiLanguageResource resource, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO SYS_MULTI_LANGUAGE_RESOURCE
            (RESOURCE_KEY, MENU_ID, LANGUAGE, VALUE)
            VALUES (@ResourceKey, @MenuId, @Language, @Value)";
        await _processor.InsertAsync(sql, LangRow.FromDomain(resource), ct);
    }

    public async Task UpdateAsync(MultiLanguageResource resource, CancellationToken ct = default)
    {
        const string sql = @"UPDATE SYS_MULTI_LANGUAGE_RESOURCE SET
            MENU_ID = @MenuId, LANGUAGE = @Language, VALUE = @Value
            WHERE RESOURCE_KEY = @ResourceKey";
        await _processor.UpdateAsync(sql, LangRow.FromDomain(resource), ct);
    }

    private sealed class LangRow
    {
        public string ResourceKey { get; set; } = "";
        public string MenuId { get; set; } = "";
        public string Language { get; set; } = "";
        public string Value { get; set; } = "";

        public MultiLanguageResource ToDomain()
        {
            if (!Enum.TryParse<LanguageType>(Language, out var lang) || !Enum.IsDefined(lang)) lang = LanguageType.KoKr;
            return MultiLanguageResource.Create(ResourceKey, MenuId, lang, Value);
        }

        public static LangRow FromDomain(MultiLanguageResource r) => new()
        {
            ResourceKey = r.Id,
            MenuId = r.MenuId,
            Language = r.Language.ToString(),
            Value = r.Value
        };
    }
}

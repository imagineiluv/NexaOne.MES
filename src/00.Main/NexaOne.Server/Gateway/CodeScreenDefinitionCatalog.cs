using System.Collections.Frozen;
using NexaOne.Web.Services.Meta;

namespace NexaOne.Server.Gateway;

/// <summary>
/// 애플리케이션 바이너리에 포함된 화면 시드만 제공하는 불변 카탈로그입니다.
/// 내부 <see cref="InMemoryScreenDefinitionProvider"/>는 생성 시 한 번만 채우고 외부에는 조회 인터페이스만
/// 노출하므로 런타임 화면 등록이나 DB 정의가 canonical 시드를 변경할 수 없습니다.
/// </summary>
public sealed class CodeScreenDefinitionCatalog : ICodeScreenDefinitionCatalog
{
    private readonly InMemoryScreenDefinitionProvider _seeds = new();
    private readonly IReadOnlySet<string> _knownUiIds;
    private readonly IReadOnlyList<ScreenDefinition> _canonicalDefinitions;

    public CodeScreenDefinitionCatalog()
    {
        var ids = _seeds.GetKnownUiIdsAsync().GetAwaiter().GetResult();
        _knownUiIds = ids.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _canonicalDefinitions = Array.AsReadOnly(ids
            .Select(_seeds.Get)
            .Where(definition => definition is not null)
            .Cast<ScreenDefinition>()
            .DistinctBy(definition => definition.UiId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(definition => definition.UiId, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    /// <summary>별칭 조회는 허용하되 반환 정의의 UiId는 항상 canonical ID를 유지합니다.</summary>
    public ScreenDefinition? Get(string uiId)
        => string.IsNullOrWhiteSpace(uiId) ? null : _seeds.Get(uiId);

    /// <summary>레거시 별칭을 포함한 불변 기본 조회 키 스냅샷을 반환합니다.</summary>
    public Task<IReadOnlySet<string>> GetKnownUiIdsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_knownUiIds);
    }

    /// <summary>별칭을 제거한 canonical 코드 시드 스냅샷을 반환합니다.</summary>
    public Task<IReadOnlyList<ScreenDefinition>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_canonicalDefinitions);
    }
}

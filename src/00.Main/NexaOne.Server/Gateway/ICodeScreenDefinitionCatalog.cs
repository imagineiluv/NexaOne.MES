using NexaOne.Web.Services.Meta;

namespace NexaOne.Server.Gateway;

/// <summary>
/// 애플리케이션 코드에 포함된 화면 정의만 조회하는 카탈로그입니다.
/// 유효 화면을 해석하는 <see cref="IScreenDefinitionProvider"/>와 분리하여 DB 정의가 코드 시드를
/// 덮어쓰는 일반 렌더링 우선순위와 무관하게 Designer가 원본 시드를 안전하게 미리 볼 수 있게 합니다.
/// </summary>
public interface ICodeScreenDefinitionCatalog
{
    ScreenDefinition? Get(string uiId);

    /// <summary>
    /// canonical 정의로 연결되는 레거시 별칭을 포함하여 모든 기본 조회 키를 반환합니다.
    /// </summary>
    Task<IReadOnlySet<string>> GetKnownUiIdsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ScreenDefinition>> ListAsync(CancellationToken ct = default);
}

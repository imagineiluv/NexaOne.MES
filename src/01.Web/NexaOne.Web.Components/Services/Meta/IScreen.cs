namespace NexaOne.Web.Services.Meta;

/// <summary>
/// 공통 화면 런타임 계약(Phase 3 — "모든 화면이 동일 Runtime 위에서 동작"). MdiTabService가 메타데이터
/// 화면과 손코딩 화면을 균일하게 호스팅하기 위한 Load/Validate/Save/Dirty 생명주기. 셸은 이 계약만 알면
/// 화면 종류와 무관하게 더티 가드·저장·정리를 일관되게 다룰 수 있다.
/// </summary>
public interface IScreen
{
    string Title { get; }
    bool IsDirty { get; }
    Task LoadAsync(CancellationToken ct = default);
    IReadOnlyList<string> Validate();
    Task<bool> SaveAsync(CancellationToken ct = default);
}

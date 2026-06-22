namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// 요청 단위 "현재 사용자" 앰비언트 컨텍스트. 미들웨어가 인증 주체로 설정하고
/// <see cref="ServiceObjectProcessor"/>가 감사 컬럼(CREATED_BY/UPDATED_BY) 주입 시 읽는다.
/// </summary>
/// <remarks>
/// <see cref="AsyncLocal{T}"/>이라 요청의 비동기 호출 사슬을 따라 흐르고, HttpContext가 없는
/// 백그라운드 서비스(아웃박스 디스패처/FDC 수집 등)에서는 설정되지 않아 <c>null</c>로 남는다 →
/// 그 경우 감사 사용자는 "SYSTEM"으로 폴백한다. 리포지토리 생성자 시그니처를 바꾸지 않고
/// 모든 감사 쓰기에 실제 사용자를 싣기 위한 단일 배선 지점이다.
/// </remarks>
public static class CurrentUserContext
{
    private static readonly AsyncLocal<string?> _userId = new();

    /// <summary>현재 인증 주체의 사용자 ID. 비인증/백그라운드 컨텍스트에서는 <c>null</c>.</summary>
    public static string? UserId
    {
        get => _userId.Value;
        set => _userId.Value = value;
    }
}

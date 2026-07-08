namespace NexaOne.ServiceContracts.Sys;

/// <summary>개인화(§20.8 조건 저장 / §20.12 즐겨찾기·최근 메뉴) 도메인 규칙의 단일 출처.
/// 모듈 서비스(NexaOne.SYS의 ConditionSettingService/UserMenuService)와 호스트 게이트웨이
/// (SysPersonalizationController)가 함께 소비한다 — 과거 호스트가 상수를 '미러'(주석 동기)하던
/// 중복을 제거(플러그인 ALC 경계 때문에 호스트는 모듈 타입을 직접 참조할 수 없어, TrackingMasterGateway
/// 선례대로 Default ALC 공유 계약(ServiceContracts)에 둔다).</summary>
public static class PersonalizationRules
{
    /// <summary>마지막 조회 조건 자동 저장 행의 예약 이름(§20.8).</summary>
    public const string LatestConditionName = "$latest";

    /// <summary>예약 조건명 접두 — '$' 시작 이름은 사용자 저장 금지($latest 보호, DB CI 콜레이션 우회 차단).</summary>
    public const char ReservedConditionPrefix = '$';

    /// <summary>메뉴(화면)당 사용자 저장 조건 상한 — 현행 App.config SaveConditionCount=10 대응.</summary>
    public const int MaxSavedConditions = 10;

    /// <summary>조건명 최대 길이.</summary>
    public const int MaxConditionNameLength = 100;

    /// <summary>메뉴 ID(화면 UiId) 최대 길이.</summary>
    public const int MaxMenuIdLength = 100;

    /// <summary>조건 값 JSON 최대 길이 — NVARCHAR(MAX) 무제한 누적 방지.</summary>
    public const int MaxValuesJsonLength = 16_384;

    /// <summary>최근 메뉴 보관 개수 — 현행 App.config RecentMenuCount=10 대응.</summary>
    public const int MaxRecentMenus = 10;

    /// <summary>즐겨찾기 상한 — 무제한 누적 방지용 웹 적응 한도(현행에는 명시 한도 없음).</summary>
    public const int MaxFavoriteMenus = 50;
}

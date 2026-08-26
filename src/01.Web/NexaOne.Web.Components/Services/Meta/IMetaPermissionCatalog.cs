using System.Security.Claims;

namespace NexaOne.Web.Services.Meta;

/// <summary>
/// 메타 화면의 바인딩 ID를 서버 실행 카탈로그의 권한으로 해석한 결과입니다.
/// <see cref="IsKnown"/>이 <see langword="true"/>이고 <see cref="RequiredPermission"/>이
/// <see langword="null"/>이면 인증 사용자에게 공개된 읽기 바인딩입니다.
/// </summary>
public readonly record struct MetaBindingPermission(bool IsKnown, string? RequiredPermission)
{
    public static MetaBindingPermission Unknown { get; } = new(false, null);

    public static MetaBindingPermission Known(string? requiredPermission)
        => new(true, string.IsNullOrWhiteSpace(requiredPermission) ? null : requiredPermission.Trim());
}

/// <summary>
/// 화면 JSON의 권한 힌트가 비어 있거나 오래돼도 실제 쿼리/명령 카탈로그를 기준으로 UX를 제어하는 확장점입니다.
/// 서버 API의 권한 검사가 최종 경계이며, 이 계약은 권한 없는 요청을 미리 보내지 않게 합니다.
/// </summary>
public interface IMetaPermissionCatalog
{
    /// <summary>읽기 쿼리를 해석합니다. 쓰기 쿼리나 알 수 없는 ID는 <see cref="MetaBindingPermission.Unknown"/>입니다.</summary>
    MetaBindingPermission ResolveRead(string queryId);

    /// <summary>쓰기 명명 쿼리 또는 typed bridge 명령을 해석합니다.</summary>
    MetaBindingPermission ResolveWrite(string commandId);
}

/// <summary>
/// ClaimsPrincipal 권한 의미를 호스트의 서버 PEP와 공유하기 위한 좁은 포트입니다.
/// Web.Components가 백엔드 구현 전체를 참조하지 않고도 같은 wildcard/manage-read 규칙을 사용하게 합니다.
/// </summary>
public interface IMetaPermissionEvaluator
{
    bool HasPermission(ClaimsPrincipal user, string requiredPermission);
}

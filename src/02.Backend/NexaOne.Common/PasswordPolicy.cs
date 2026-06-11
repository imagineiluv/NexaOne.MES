using System.Text.RegularExpressions;

namespace NexaOne.Common;

/// <summary>비밀번호 복잡도 정책 — 설계서 §19.2.2. 서버가 최종 검증하며(실패 시 400),
/// 클라이언트 검증은 안내용이다. UserService는 해시만 받으므로 평문이 존재하는
/// API 계층(AuthController)에서 호출한다.</summary>
public static class PasswordPolicy
{
    /// <summary>§19.2.2 서버 검증 정규식 기본값 — 최소 8자, 대/소문자·숫자·특수문자 각 1자 이상.</summary>
    public static readonly Regex Pattern = new(
        @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[!@#$%^&*?_~\-\(\)]).{8,}$",
        RegexOptions.Compiled);

    /// <summary>§19.2.6 — 정책 위반 400 응답의 code 값.</summary>
    public const string ErrorCode = "PASSWORD_POLICY_VIOLATION";

    /// <summary>정책 위반 시 사유 메시지를, 통과 시 null을 반환한다.</summary>
    public static string? Validate(
        string password,
        string? userId = null,
        string? userName = null,
        string? email = null)
    {
        // §19.2.2 — 앞/뒤 공백 금지
        if (password.Length == 0 || password != password.Trim())
            return "비밀번호는 앞뒤 공백 없이 입력해야 합니다.";

        if (!Pattern.IsMatch(password))
            return "비밀번호는 8자 이상이며 대문자, 소문자, 숫자, 특수문자(!@#$%^&*?_~-())를 각각 1자 이상 포함해야 합니다.";

        // §19.2.2 — USER_ID, USER_NAME, 이메일 local-part 포함 금지.
        // 2자 이하 토큰은 우연 일치가 잦아 제외한다 (예: 이름 이니셜).
        var emailLocalPart = email?.Split('@')[0];
        foreach (var token in new[] { userId, userName, emailLocalPart })
        {
            if (!string.IsNullOrWhiteSpace(token) && token.Length >= 3
                && password.Contains(token, StringComparison.OrdinalIgnoreCase))
                return "비밀번호에 사용자 ID, 이름, 이메일을 포함할 수 없습니다.";
        }

        return null;
    }
}

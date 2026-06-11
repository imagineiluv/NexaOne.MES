using NexaOne.Common;

namespace NexaOne.UnitTests.Common;

/// <summary>§19.2.2 — 비밀번호 복잡도 정책 서버 검증(PasswordPolicy)을 검증한다.
/// 정규식(8자+대/소/숫자/특수), 앞뒤 공백 금지, 사용자 정보 포함 금지.</summary>
public sealed class PasswordPolicyTests
{
    // ── 정규식 통과 ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("NewPw123!")]
    [InlineData("Abcdef1?")]          // 정확히 8자
    [InlineData("Str0ng-Pass")]       // 하이픈 특수문자
    [InlineData("X9y8(z7w)")]         // 괄호 특수문자
    [InlineData("Very_Long_Passw0rd~")]
    public void Validate_compliant_password_returns_null(string password)
    {
        PasswordPolicy.Validate(password).Should().BeNull();
    }

    // ── 정규식 위반 ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Short1!")]           // 7자 — 길이 미달
    [InlineData("nouppercase1!")]     // 대문자 없음
    [InlineData("NOLOWERCASE1!")]     // 소문자 없음
    [InlineData("NoDigitsHere!")]     // 숫자 없음
    [InlineData("NoSpecial123")]      // 특수문자 없음
    public void Validate_non_compliant_password_returns_reason(string password)
    {
        PasswordPolicy.Validate(password).Should().Contain("8자 이상");
    }

    // ── 공백 규칙 ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(" NewPw123!")]
    [InlineData("NewPw123! ")]
    [InlineData("")]
    public void Validate_leading_trailing_whitespace_or_empty_returns_reason(string password)
    {
        PasswordPolicy.Validate(password).Should().Contain("공백");
    }

    [Fact]
    public void Validate_inner_whitespace_is_allowed_when_pattern_matches()
    {
        // 내부 공백은 금지 대상이 아니다 — 앞뒤 공백만 금지
        PasswordPolicy.Validate("New Pw123!").Should().BeNull();
    }

    // ── 사용자 정보 포함 금지 ─────────────────────────────────────────────────

    [Fact]
    public void Validate_password_containing_userId_returns_reason()
    {
        PasswordPolicy.Validate("Xu001Pw123!", userId: "u001")
            .Should().Contain("포함할 수 없습니다");
    }

    [Fact]
    public void Validate_password_containing_userName_case_insensitive_returns_reason()
    {
        PasswordPolicy.Validate("MyALICEpw123!", userName: "Alice")
            .Should().Contain("포함할 수 없습니다", "대소문자를 무시하고 검사해야 한다");
    }

    [Fact]
    public void Validate_password_containing_email_local_part_returns_reason()
    {
        PasswordPolicy.Validate("alice!Pw123", email: "alice@test.com")
            .Should().Contain("포함할 수 없습니다", "이메일 local-part(alice)가 포함되면 거부해야 한다");
    }

    [Fact]
    public void Validate_short_tokens_under_3_chars_are_not_checked()
    {
        // 2자 이하 토큰은 우연 일치가 잦아 검사 대상에서 제외한다
        PasswordPolicy.Validate("AbcPw123!", userId: "ab", userName: "Bc")
            .Should().BeNull();
    }

    [Fact]
    public void Validate_clean_password_with_user_info_returns_null()
    {
        PasswordPolicy.Validate("NewPw123!", "u001", "Alice", "alice@test.com")
            .Should().BeNull();
    }
}

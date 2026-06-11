namespace NexaOne.SYS.Domain;

/// <summary>설계서 §19.2.1 — 사용자 비밀번호 상태. Normal 외 상태는 로그인 후 비밀번호 변경을 강제한다 (§20.10).</summary>
public enum PasswordState
{
    /// <summary>정상 — 추가 조치 불필요.</summary>
    Normal,

    /// <summary>관리자 신규 생성 직후 — 최초 로그인 시 변경 강제.</summary>
    Create,

    /// <summary>비밀번호 분실로 임시 비밀번호 발급됨 — 로그인 시 변경 강제 (§20.10).</summary>
    Forgot,

    /// <summary>비밀번호 유효기간 만료 (§19.2.5).</summary>
    Expired
}

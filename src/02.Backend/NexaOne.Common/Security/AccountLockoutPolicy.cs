namespace NexaOne.Common.Security;

/// <summary>§20.10 — 연속 로그인 실패 잠금 정책(단일 출처). SYS 도메인(User)과 통합 호스트 인증 SQL이
/// 동일 값을 공유해 드리프트를 차단한다(Phase 3b 설계 §9 완화).</summary>
public static class AccountLockoutPolicy
{
    /// <summary>연속 실패 잠금 임계값(5회).</summary>
    public const int MaxConsecutiveFailures = 5;

    /// <summary>계정 잠금 시간(30분).</summary>
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(30);
}

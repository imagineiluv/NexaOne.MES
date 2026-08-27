# 보전 작업자 로그인 projection을 SYS에 둔다

- 상태: Accepted (temporary physical-schema exception)
- 결정일: 2026-08-28
- 소유자: SYS
- 검토 기한: 2026-11-30

## 배경과 결정

EMS는 로그인 사용자를 보전 작업자에 연결해야 한다. 인증 사용자 활성 상태는 SYS가 소유하지만 기존 매핑
테이블 이름은 `MDM_WORKER_USER_MAP`이다. 호스트에 SQL을 두지 않고 identity 의미를 한 경계에서 보장하기 위해
`MaintenanceIdentityDirectory`를 SYS Infrastructure에 두고 이 정확한 매핑 테이블과 SYS 사용자만 조회한다.

architecture test는 `MaintenanceIdentityDirectory.cs`와 `MDM_WORKER_USER_MAP` 한 쌍만 허용한다. 다른 SYS source,
다른 MDM 테이블, 또는 쓰기는 허용하지 않는다.

## 제거 조건

매핑을 SYS 소유 테이블로 이관하거나 MDM이 유효기간 매핑 directory를 제공하면 직접 참조와 allowlist를 함께
삭제한다. 이관 시 기존 유효기간 중복 검증과 로그인 사용자 활성 판정을 보존한다.

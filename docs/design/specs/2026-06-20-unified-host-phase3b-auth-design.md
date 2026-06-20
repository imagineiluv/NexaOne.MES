# 통합 호스트 Phase 3b — 인증/토큰 발급 설계 (게이트웨이식, 무-브리지)

> 상태: 승인 대기(브레인스토밍 산출, 적대적 검증 반영) · 작성일 2026-06-20
> 상위: [통합 호스트 설계](2026-06-20-unified-host-design.md) §5(하이브리드)·§7(Phase 3). ADR-003(권한 PEP).

## 1. 목적
통합 호스트(NexaOne.Server)가 **토큰을 직접 발급**(login/refresh)하도록 한다 — 현재 호스트는 JWT 검증만 하고 발급이 없어 실제 클라이언트가 로그인할 수 없다. plugin↔DI 브리지 없이(게이트웨이-최대), 보안 동작은 기존 NexaOne.API와 동일하게 유지한다.

## 2. 접근 (게이트웨이식, 브리지 없음)
로그인/리프레시의 **데이터 읽기·쓰기는 명명 쿼리(게이트웨이, Default-ALC IRuleDispatcher)**로, **암호·토큰 로직은 이미 Default-ALC인 타입**으로 처리한다. NexaOne.SYS 플러그인 타입을 런타임에 쓰지 않는다.

검증된 근거:
- `PasswordHasher`(NexaOne.Common, static, PBKDF2-HMAC-SHA256 100k + `FixedTimeEquals`) — Default-ALC, 그대로 사용.
- `Permissions`·`RolePermissionDefaults`(NexaOne.Common.Security) — Default-ALC. `UserService.GetEffectivePermissionsAsync`는 정확히 `RolePermissionDefaults.For(roleId) ∪ split(SYS_ROLE.PERMISSIONS,'|')`이므로 호스트에서 재현 가능.
- `IJwtService`/`JwtService`·`IRefreshTokenStore`/`RefreshTokenStore`는 **NexaOne.API(실행 웹앱)에만** 존재 → Default-ALC 공유 어셈블리로 **이동** 필요.

## 3. ALC 이동 (정확성 핵심)
- **이동**: `IJwtService`+`JwtService`, `IRefreshTokenStore`+`RefreshTokenStore`(+Redis 변형) → **`NexaOne.Application`**(이미 Default-ALC이고 IConfiguration 의존 수용; NexaOne.Common은 IConfiguration 무의존이라 부적합 — open question 해소). 인터페이스+구현 동일 어셈블리 유지(DIP/ALC 경계). `JwtService.PasswordChangeClaim` 상수 보존. NexaOne.API는 이 어셈블리를 참조하므로 기존 API 동작 무변경.
- **이동 안 함**: PasswordHasher·Permissions·RolePermissionDefaults(이미 Default-ALC). UserService/PasswordResetService/SYS 리포지토리(플러그인 — 호스트 인증 경로 미사용; 동작은 SYS 명명쿼리로 재현).
- **DI**: 신규 `AddNexaOneAuth(IConfiguration)`(이동된 어셈블리)가 IJwtService·IRefreshTokenStore·신규 `GatewayLoginService`를 등록. NexaOne.Server가 `AddNexaOneGateway` 옆에서 호출. **`AddNexaOneServices`(API)는 호출 금지**(9개 플러그인 모듈 등록을 끌어옴).

## 4. 로그인 흐름 (하드닝 — 검증 결함 반영)
`GatewayLoginService`는 **`IQueryRegistry`로 명명쿼리 id→방언 SQL 해석 후 `IRuleDispatcher`로 디스패치**한다(게이트웨이 컨트롤러와 동일 — `IRuleDispatcher`는 raw SQL을 받으므로 직접 id 호출 불가). 흐름:
1. HttpContext에서 IP·User-Agent 추출.
2. `SYS.AuthUserById`(@userId) 1행 조회(SYS_USER ⋈ SYS_ROLE). 없으면 `SYS.InsertLoginFailureHist`(UserNotFound) + 401 `INVALID_CREDENTIALS`(열거 방지).
3. now=UtcNow. `LOCKED_UNTIL`이 now보다 미래면 실패이력 + 401 `ACCOUNT_LOCKED`(잔여분).
4. `IS_ACTIVE=0 || IS_DELETED=1`이면 실패이력 + 401 `INVALID_CREDENTIALS`.
5. `!PasswordHasher.Verify(pw, PASSWORD_HASH)`: `SYS.RecordLoginFailure`(아래 **잠금 패리티** 적용) → 결과 LOCKED_UNTIL 재평가 → 실패이력 + 401(잠김이면 ACCOUNT_LOCKED, 아니면 INVALID_CREDENTIALS).
6. 성공: `requirePwdChange = (PASSWORD_STATE != 'Normal')`. `NeedsRehash`면 `SYS.RecordLoginSuccess @passwordHash=Hash(pw)`(아니면 NULL → `COALESCE`). 이 단일 UPDATE가 LAST_LOGIN_AT·FAIL_COUNT=0·LOCKED_UNTIL=NULL 동시 처리. PASSWORD_STATE는 보존.
7. `perms = distinct(RolePermissionDefaults.For(ROLE_ID) ∪ split(PERMISSIONS,'|'))` — **PERMISSIONS NULL(역할 소프트삭제 등)이면 빈 집합으로 두되 RolePermissionDefaults는 적용**(ADMIN '*' 보존).
8. `accessToken = IJwtService.GenerateAccessToken(...)`, `refreshToken = IRefreshTokenStore.IssueAsync(USER_ID)`(해시 저장 — §5).
9. 200 LoginResponse(기존 API와 동일 DTO/계약).

## 5. 명명 쿼리 (신규 `db/queries/{mssql,sqlite}/SYS.xml` — 현재 없음)
- `SYS.AuthUserById`(read): SYS_USER U LEFT JOIN SYS_ROLE R. 컬럼: USER_ID·USER_NAME·PASSWORD_HASH·EMAIL·ROLE_ID·LANGUAGE·IS_ACTIVE·IS_DELETED·LAST_LOGIN_AT·PASSWORD_STATE·FAIL_COUNT·LOCKED_UNTIL·ROLE_NAME·PERMISSIONS. WHERE USER_ID=@userId. (MSSQL NOLOCK / SQLite 무힌트.)
- `SYS.RecordLoginFailure`(write): **잠금 패리티** — 운영 `UserRepository.RecordLoginFailureAsync`와 동일하게, 만료 잠금(LOCKED_UNTIL≤now)이면 FAIL_COUNT를 1로 리셋, 아니면 +1; 결과 ≥5면 LOCKED_UNTIL=now+30분. (CASE 식으로 단일 UPDATE; 무조건 증가 금지.)
- `SYS.RecordLoginSuccess`(write): LAST_LOGIN_AT=@utcNow, FAIL_COUNT=0, LOCKED_UNTIL=NULL, PASSWORD_HASH=COALESCE(@passwordHash,PASSWORD_HASH). PASSWORD_STATE 미변경.
- `SYS.InsertLoginFailureHist`(write): SYS_LOGIN_FAILURE_HIST 감사(이유별). 비존재 사용자도 기록(FK 없음).
- `SYS.InsertRefreshToken`/`SYS.ValidateRefreshToken`/`SYS.RevokeRefreshToken`/`SYS.RevokeAllRefreshTokens`(신규 SYS_REFRESH_TOKEN): **TOKEN_HASH=SHA-256(opaque token) 저장(평문 금지)**, USER_ID·EXPIRES_AT·REVOKED_AT·CREATED_AT. 조회는 해시 인덱스 등가(타이밍 안전).
모든 쿼리 mssql/sqlite 두 벌(기존 db/queries 규약).

## 6. 마이그레이션 `V034__SYS_REFRESH_TOKEN.sql`
- `SYS_REFRESH_TOKEN`(TOKEN_ID PK, USER_ID, TOKEN_HASH, EXPIRES_AT, REVOKED_AT NULL, CREATED_AT) + (USER_ID, TOKEN_HASH) 인덱스.
- **`SYS_USER.PASSWORD_HASH` 폭 확장**: 현재 NVARCHAR(64) → NVARCHAR(255). PBKDF2 문자열(~83자)이 64를 초과해 MSSQL에서 rehash 쓰기가 throw하므로 rehash 활성화 전 선행 필수(SQLite는 길이 무시로 가려짐 — MSSQL 실버그).

## 7. 트랜잭션 원자성 (ALC 제약 반영)
게이트웨이 디스패처에는 다중문 트랜잭션 원시가 없다(`ServiceObjectProcessor.ExecuteManyAsync`는 플러그인 Infrastructure, 호스트 미도달). 따라서:
- 잠금·성공 갱신은 **단일 조건부 UPDATE**로 원자화(위 §5).
- 리프레시 회전(old revoke + new insert)은 **조건부 UPDATE…OUTPUT/RETURNING로 재사용 탐지**(이전 토큰이 이미 revoked면 회전 실패 → 재생 공격 방어; 인메모리 저장소보다 강화).
- 실패-증가와 실패-이력-insert는 별도 디스패치(이력은 감사용이라 비원자 허용; 단 명시).

## 8. 엔드포인트 (Phase 3b 최소)
- **구현**: `POST /api/v1/auth/login`·`POST /api/v1/auth/refresh`(둘 다 [AllowAnonymous]+[EnableRateLimiting("auth")]). 신규 `AuthController`(NexaOne.Server.Gateway), 기존 API와 동일 DTO/라우트/상태코드.
- **연기**: logout·change-password·forgot/reset·register·me(후속 — Phase 3b는 발급/검증 무-브리지 입증이 목표).
- **레이트리미터**: 통합 호스트에 현재 없음 → API와 동일 `"auth"` 정책(IP당 10/min) + 전역(100/min) + `UseRateLimiter()` 도입(브루트포스 회귀 방지). 오류 본문은 `NexaOne.Common.Error` 형태 통일.

## 9. 위험·미해결 (검증 반영)
- **도메인 로직 중복**(잠금 5/30·rehash·requirePwdChange·active/deleted): SYS 도메인과 호스트 SQL 두 출처 → 드리프트 위험. 완화: 상수를 NexaOne.Common에 중앙화 + API↔호스트 동작 동일성 교차 통합테스트.
- **plantId on /refresh**: AllowAnonymous라 옛 Bearer 헤더에서 복구 — 클라이언트가 헤더 미전송 시 DEFAULT로 저하. refresh DTO에 plantId 추가 또는 refresh 토큰에 plantId 저장 검토.
- **dual-issuer 전환기**: API와 호스트가 동시 발급하면 SYS_REFRESH_TOKEN 공유 필수(한쪽 발급→다른쪽 refresh). Jwt:SecretKey/Issuer/Audience 단일 출처 필수(드리프트=전역 401).
- **로그인 타이밍**: UserNotFound는 빠른 401(PBKDF2 미실행) → 사용자 존재 타이밍 누출. 운영도 동일(회귀 아님). 하드닝 시 더미 verify 검토.
- **만료 토큰 정리**: SYS_REFRESH_TOKEN 보존 전략(EXPIRES_AT 필터 + 주기 정리) 추후.

## 10. 단계화 (Phase 3b 내부)
1. ALC 이동: JwtService/RefreshTokenStore → NexaOne.Application(+API 참조 갱신, API 테스트 회귀 확인). 
2. V034 마이그레이션(SYS_REFRESH_TOKEN + PASSWORD_HASH 확장) + SYS.xml 명명쿼리(잠금 패리티 SQL).
3. 게이트웨이 backed `SysRefreshTokenStore`(IRuleDispatcher+IQueryRegistry; 인메모리는 테스트용 유지).
4. `GatewayLoginService` + `AuthController`(login/refresh) + 레이트리미터 + DI(AddNexaOneAuth) in NexaOne.Server.
5. 통합테스트: login 성공/오류(열거 방지)/잠금(패리티)/refresh 회전/재생 방어/권한 클레임 — SQLite E2E. API 회귀 무영향.

## 11. 검증
- SQLite + NexaMes 스키마로 자동 E2E(modules OFF, 게이트웨이 경로). login→보호 엔드포인트 호출, 오류·잠금·refresh 회전, 권한 403.
- 기존 NexaOne.API 인증 회귀 무영향(타입 이동만, 동작 동일) — API 통합테스트 녹색 확인.

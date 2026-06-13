# ADR-003 — Security Engine (PEP) — 모든 권한이 단일 정책 집행점을 통과

- **상태**: 채택 (2026-06-13)
- **관련**: [Frontend-Coexistence-GapAnalysis.md](../Frontend-Coexistence-GapAnalysis.md) §2.6, Phase 1A
- **결정자**: 사용자 승인

## 컨텍스트

비전은 "모든 권한이 Security Engine(정책 집행점, PEP)을 통과"한다. 현재:
- 강한 building block: JWT(약한키 부팅거부), PBKDF2 해시, RateLimiting, 계정 잠금, 미들웨어.
- 그러나 인가는 **하드코딩 역할명**(`[Authorize(Roles="ADMIN")]`/`"ADMIN,OPERATOR"`)에 분산.
- `Role.Permissions`가 도메인·DB(`SYS_ROLE.PERMISSIONS`, `|` 구분)에 **정의·영속화되고 Add/Remove API까지 있으나 인가에 미사용**.
- JWT가 `RoleId`만 `ClaimTypes.Role`로 싣고 **permission 클레임 미발급**. 커스텀 정책/핸들러 0건.

### 설계 분기
권한 기반 인가로 가려면 **권한 분류 체계(taxonomy)**와 엔드포인트↔권한 매핑이 필요하다.

## 결정

**permission 기반 인가를 추가형(additive)으로 도입한다.** 기존 역할 기반 `[Authorize]`를 **깨지 않고** permission 정책을 병행한다:

1. **권한 분류**: `module:action` 규약(예: `fdc:control`, `mdm:read`, `sys:user.manage`). `Permissions.cs` 상수 카탈로그로 정의.
2. **클레임 발급**: `JwtService.GenerateAccessToken`이 사용자 역할의 `Role.Permissions`를 조회해 `permission` 클레임으로 다중 발급(역할 클레임은 유지).
3. **PEP**: 커스텀 `IAuthorizationPolicyProvider`(`perm:{permission}` 정책을 동적 생성) + `PermissionRequirement`/`PermissionAuthorizationHandler`(permission 클레임 매칭). `[Authorize(Policy="perm:fdc:control")]`로 집행.
4. **하위호환**: 역할↔권한 기본 매핑(ADMIN=전체, OPERATOR=운영 권한…)을 시드해, 기존 역할 사용자가 적절한 permission을 자동 보유. 기존 `[Authorize(Roles=...)]`는 그대로 동작.

## 접근(구현 범위)

- 신규(NexaOne.Common 또는 SYS): `Permissions`(상수 카탈로그), `PermissionRequirement`, `PermissionAuthorizationHandler`, `PermissionPolicyProvider`.
- `JwtService`/`IJwtService`: 토큰 생성 시 permission 목록 수용 → `permission` 클레임 발급. `AuthController`가 로그인 사용자 역할의 권한을 조회해 전달(역할→권한 해석은 `IRoleRepository`/`Role.Permissions`).
- Program.cs: `AddAuthorization` + `IAuthorizationPolicyProvider`/`IAuthorizationHandler` 등록.
- **대표 슬라이스**: PEP 인프라(정책 provider/handler/requirement) + 클레임 발급 + 권한 카탈로그 + 한 컨트롤러(FDC 설비 제어)를 `[Authorize(Policy="perm:fdc:control")]`로 전환(역할 매핑 시드로 ADMIN/OPERATOR 호환) + 단위 테스트. 나머지 컨트롤러의 역할→정책 전환은 **점진 후속**.

## 결과

- **장점**: 게이트웨이형 PEP 확보, `Role.Permissions` 활성화(RBAC가 role-name 체크를 넘어 권한 집행), 추가형이라 무중단·가역, 세밀 권한 확장 토대.
- **비용/위험**: 권한 분류·역할 매핑 시드 정의 필요. 클레임 수 증가(토큰 크기) — 권한 수가 많아지면 압축/참조 전략 후속.
- **비채택**: 역할명 하드코딩 유지(비전 미달), 외부 IdP/OIDC 즉시 도입(범위 과대 — LdapDriver 연동은 후속).

# 불필요 프로젝트 삭제 계획 (단계적 은퇴)

> 상태: 계획(실행 전). 작성 2026-06-21. 파괴적 작업이므로 단계별로 사용자 승인 후 실행한다.

**목표:** 통합 호스트(NexaOne.Server)가 흡수해 불필요해진 4개 프로젝트를 위험·노력 순으로 단계 은퇴한다 — NexaOne.Driver.MemoryCache, NexaOne.Driver.Redis, NexaOne.Web(exe), NexaOne.API(exe). 각 단계는 빌드·테스트 녹색을 게이트로 두고, 커버리지 손실을 명시적으로 결정한다.

**범위 제외(유지):** NexaOne.Server, NexaOne.Web.Components(RCL — 호스트가 /meta로 사용), NexaOne.Application/Common/Infrastructure/Infrastructure.Messaging/ServiceContracts, 9개 도메인 모듈, 3개 테스트 프로젝트. (이름이 비슷한 **NexaOne.Web.Components는 살아있는 RCL**이라 삭제 대상 아님 — exe인 NexaOne.Web과 구분.)

---

## 검증된 제약 (직접 확인, 2026-06-21 — 단계 설계의 근거)

참조 그래프(전 .csproj ProjectReference) + 런타임 로드(Spring/config grep) + 솔루션 등록(NexaOne.sln) 실측:

1. **NexaOne.Driver.MemoryCache — 완전 고아.** 어떤 .csproj도 참조하지 않음(ProjectReference 0건), Spring/config 런타임 로드 없음. 호스트 캐시는 `NexaOne.Common.Caching.MemoryCacheService`(Common). → 위험 0, 즉시 삭제 가능.
2. **NexaOne.Driver.Redis — API 전용.** `NexaOne.API`만 참조([NexaOne.API.csproj], `RedisRefreshTokenStore.cs`/`ServiceCollectionExtensions.cs`). 호스트는 DB기반 `SysRefreshTokenStore`라 Redis 미사용. → API와 동반 은퇴.
3. **NexaOne.Web(exe) — 운영 참조 0, 테스트만.** `NexaOne.UnitTests`만 참조. UI 핵심(메타 런타임·렌더러·인증/ApiClient)은 RCL(`NexaOne.Web.Components`)로 추출돼 호스트가 사용. 단 exe에는 손코딩 화면·앱 셸이 남아 있고, **UnitTests `Web/` 폴더(~20파일)는 exe-결합 vs RCL-결합이 혼재**(RCL은 RootNamespace=NexaOne.Web 보존이라 `using NexaOne.Web.*`가 둘 다 해석됨) → 파일별 분류 필요.
4. **NexaOne.API(exe) — 통합테스트 호스트.** [TestApiFactory.cs:14](../../../test/NexaOne.IntegrationTests/TestApiFactory.cs#L14) `WebApplicationFactory<Program>`의 Program이 **NexaOne.API**다. 즉 **통합테스트 전체(~289개)가 API 인프로세스로 구동**된다. 추가로 UnitTests의 컨트롤러/서비스/미들웨어 테스트 ~17파일이 API 타입 참조. → 삭제 = 통합 테스트 스위트 이관/은퇴라는 대규모 작업.

**솔루션:** 위 4개 모두 NexaOne.sln에 등록됨(빌드/CI 범위). 제거 시 `dotnet sln remove`로 GUID·구성 섹션 정리.

## 메커니즘 공통 규약 (모든 단계)
- 프로젝트 제거: `dotnet sln NexaOne.sln remove <csproj>` → `git rm -r <projDir>`.
- 의존 테스트 csproj에서 해당 `<ProjectReference>` 라인 삭제(Edit).
- 검증: `dotnet build NexaOne.sln -c Debug`(0 errors) + 영향 테스트 프로젝트 `dotnet test`(녹색).
- 커밋: PowerShell BOM-free, `git add -A` 금지(submodules/NexusLogic 더티) — 명시 경로만(삭제는 `git rm`이 스테이징). 단계별 브랜치 → main ff-merge(sln 아티팩트 가드: `git checkout -- NexaOne.sln` 후 ff-merge), push는 사용자 요청 시. Co-Authored-By 트레일러.
- 각 단계는 독립 PR/브랜치로 분리(롤백 용이).

---

## Phase D1 — NexaOne.Driver.MemoryCache 제거 (위험 0, 즉시)

**근거:** 참조 0건·런타임 로드 없음. 순수 dead.

- [ ] Step 1: `dotnet sln NexaOne.sln remove src/03.Driver/03.Cache/NexaOne.Driver.MemoryCache/NexaOne.Driver.MemoryCache.csproj`
- [ ] Step 2: `git rm -r src/03.Driver/03.Cache/NexaOne.Driver.MemoryCache`
- [ ] Step 3: `dotnet build NexaOne.sln -c Debug --nologo` → 0 errors(아무 의존도 없어 무영향).
- [ ] Step 4: 전체 테스트 스모크(UnitTests/IntegrationTests/ServerTests 빌드만이라도) → 영향 없음 확인.
- [ ] Step 5: 커밋 `chore(cleanup): 미사용 NexaOne.Driver.MemoryCache 프로젝트 제거(참조 0)`.

**위험:** 없음. **결정 불요.**

---

## Phase D2 — NexaOne.Web(exe) 은퇴 (소~중)

**근거:** 운영 참조 0(호스트는 RCL 사용). 단 exe 화면·앱 셸 + UnitTests Web 결합 정리 필요.

**선결 결정(사용자):** NexaOne.Web exe의 손코딩 화면(셸/MDI/레거시 화면)은 호스트에서 서빙되지 않는다(호스트는 /meta[RCL] + /spa[React]만). 이 화면들을 (a) 폐기(메타/SPA로 대체 완료 간주) 할지, (b) 호스트로 이관 후 삭제 할지 결정. 폐기(a)면 아래 진행, 이관(b)면 별도 선행 트랙.

- [ ] Step 1: **UnitTests `Web/` 파일 분류.** 각 `test/NexaOne.UnitTests/Web/*.cs`가 참조하는 타입이 RCL(`NexaOne.Web.Components`)에 있는지 exe(`NexaOne.Web`)에 있는지 판정한다(타입을 Web.Components/ 와 NexaOne.Web/ 에서 각각 Glob/Grep로 확인). 분류 방법: 파일이 쓰는 컴포넌트/서비스 타입을 `src/01.Web/NexaOne.Web.Components/`에서 찾으면 RCL-결합(유지), `src/01.Web/NexaOne.Web/`에만 있으면 exe-결합(제거 대상). 예상: 메타 런타임·렌더러·Jwt/Auth/ScreenDefinition 테스트는 RCL-결합(유지), 앱 셸·MDI·레거시 화면 테스트는 exe-결합.
- [ ] Step 2: exe-결합 테스트 파일 삭제(`git rm`). RCL-결합 테스트는 유지(이미 Web.Components를 통해 컴파일됨 — UnitTests가 Web.Components를 참조하는지 확인하고, 미참조면 `NexaOne.Web.Components` ProjectReference를 추가해 RCL-결합 테스트가 exe 없이도 빌드되게 한다).
- [ ] Step 3: `test/NexaOne.UnitTests/NexaOne.UnitTests.csproj`에서 `<ProjectReference ...NexaOne.Web\NexaOne.Web.csproj />` 라인 삭제. (필요 시 NexaOne.Web.Components 참조 추가.)
- [ ] Step 4: `dotnet sln NexaOne.sln remove src/01.Web/NexaOne.Web/NexaOne.Web.csproj` → `git rm -r src/01.Web/NexaOne.Web`.
- [ ] Step 5: `dotnet build NexaOne.sln -c Debug` + `dotnet test test/NexaOne.UnitTests` → 녹색(유지 테스트가 RCL로 컴파일). UnitTests 카운트 감소분(=삭제한 exe-결합 테스트 수) 기록.
- [ ] Step 6: 커밋 `chore(cleanup): NexaOne.Web(exe) 은퇴 — UI는 RCL(Web.Components)·호스트가 흡수, exe-결합 테스트 정리`.

**위험:** 중. UnitTests Web 분류 오판 시 RCL-결합 테스트가 깨질 수 있음 → Step 1 분류를 빌드로 검증. 손코딩 화면 폐기 결정이 선결.

---

## Phase D3 — NexaOne.API(exe) + NexaOne.Driver.Redis 은퇴 (대)

**근거:** API는 흡수됐으나 **통합테스트 전체(~289)의 인프로세스 호스트**(TestApiFactory) + UnitTests ~17파일. 단순 삭제 불가 — 커버리지 처리 결정 필수.

**선결 결정(사용자) — 통합 커버리지 처리:**
- **D3a(권장·고비용): 통합테스트를 호스트로 이관.** `TestApiFactory`를 `WebApplicationFactory<NexaOne.Server.Program>` 기반으로 재작성하고, ~289 테스트를 API REST(타입 컨트롤러)에서 호스트 게이트웨이(`/api/v1/query|command/{id}`)·브리지(`/api/v1/est|rms`)·인증으로 재매핑한다. 타입 REST가 명명쿼리로 대체된 경로는 1:1 매핑이 없어 재작성 필요. 모듈 도메인 로직 커버리지는 보존됨.
- **D3b(저비용·커버리지 감소): API + 통합테스트 + API-결합 UnitTests 일괄 삭제.** 호스트 커버리지는 ServerTests(현재 71)에 의존. 통합테스트 ~289 손실 — 모듈 도메인 검증 공백 발생(수용 여부 결정).
- 중간안(D3c): 통합테스트 중 **게이트웨이로 이미 표현 가능한 부분만 호스트로 이관**(예: MDM/QMS 명명쿼리 E2E는 ServerTests 패턴 존재), 타입 전용 REST 테스트는 폐기. 점진.

**실행(결정 후 공통 뼈대):**
- [ ] Step 1: (D3a 택1) `TestApiFactory`를 호스트 Program 기반으로 재작성 + 테스트 재매핑(별도 대형 트랙, 게이트웨이/브리지 엔드포인트로). / (D3b 택2) `test/NexaOne.IntegrationTests` 전체 또는 API-의존 테스트 삭제.
- [ ] Step 2: `test/NexaOne.UnitTests`에서 API-결합 ~17파일(Controllers/·일부 Services/·Middleware/PasswordChangeRequiredMiddlewareTests 등 — `using NexaOne.API` 보유 파일) 삭제. 호스트 동등 커버리지는 ServerTests(GatewayAuth*·PasswordChangeGate·Bridge·RefreshTokenCleanup 등)에 존재함을 대조 확인.
- [ ] Step 3: UnitTests/IntegrationTests csproj에서 `NexaOne.API`(및 D3b면 관련) ProjectReference 삭제.
- [ ] Step 4: `dotnet sln remove` + `git rm -r` 로 `NexaOne.API` 제거. 이어서 **NexaOne.Driver.Redis** 동일 제거(소비처가 API뿐 — 이 시점 고아). `RedisRefreshTokenStore`/Redis 등록은 API와 함께 사라짐.
- [ ] Step 5: `dotnet build NexaOne.sln` + 전체 테스트(남은 UnitTests·ServerTests, D3a면 이관된 IntegrationTests) 녹색. 커버리지 변화(삭제/이관 테스트 수) 기록.
- [ ] Step 6: 커밋 `chore(cleanup): NexaOne.API(exe)+Driver.Redis 은퇴 — 호스트 게이트웨이/브리지로 대체` (+ D3a면 통합테스트 이관 커밋 분리).

**위험:** 대. 통합테스트 ~289의 운명이 핵심. D3b는 빠르지만 모듈 도메인 통합 커버리지 큰 손실 — **사용자 승인 필수**. D3a는 안전하지만 별도 대형 트랙(사실상 통합테스트 마이그레이션 프로젝트).

---

## 권장 순서 / 요약

| 단계 | 대상 | 노력 | 위험 | 선결 결정 |
|---|---|---|---|---|
| **D1** | Driver.MemoryCache | 분 | 0 | 없음 — 바로 가능 |
| **D2** | NexaOne.Web(exe) | 소~중 | 중 | 손코딩 화면 폐기 vs 이관 |
| **D3** | NexaOne.API + Driver.Redis | 대 | 대 | 통합테스트 ~289 이관(D3a)/폐기(D3b) |

- **즉시 실행 권장: D1만**(순수 고아, 무위험). 단건 PR.
- **D2**: 손코딩 화면 폐기 결정 확정 시 진행(테스트 파일별 분류가 핵심 작업).
- **D3**: 통합테스트 커버리지 결정(D3a 이관 / D3b 폐기 / D3c 점진) 없이는 착수 불가 — 사실상 별도 프로젝트. 결정 전까지 API/Redis 유지.

## Self-Review
- 4개 대상 모두 근거(참조 그래프·런타임 grep·sln) 제시. RCL(Web.Components)을 exe(Web)와 명확히 분리(오삭제 방지). ✓
- 파괴성 명시: 단계별 사용자 승인, 빌드·테스트 게이트, 브랜치 분리·롤백 용이. 실행 아님(계획). ✓
- 핵심 리스크 정직 표기: API=통합테스트 호스트(~289), Web=화면 폐기 결정, 테스트 파일별 분류 필요. 플레이스홀더 없이 실제 경로·명령·결정지점 명시. ✓
- 미결: D2 손코딩 화면 처리·D3 통합 커버리지 전략은 사용자 결정 사항으로 분리(임의 진행 금지).

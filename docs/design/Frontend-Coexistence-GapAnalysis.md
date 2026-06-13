# Frontend Coexistence (NAF Studio) — 갭 분석 및 단계별 로드맵

> 대상 비전: [Frontend-Coexistence.md](./Frontend-Coexistence.md) — Low-Code + Pro-Code(React/Vue) 공존,
> 모든 화면이 동일 Runtime, 모든 데이터가 Query Engine, 모든 이벤트가 Event Bus, 모든 권한이 Security Engine 통과.
> 분석 범위: NexaMes(src) + 서브모듈 NexusFramework / NexusCom. 작성: 2026-06-13 (적대적 검증 워크플로우 7-에이전트 인벤토리 기반).

## 1. 요약 (Executive Summary)

비전은 **백엔드 실행 원형(primitive)은 상당히 갖췄으나, "엔진을 게이트웨이로 강제"하는 핵심 아키텍처와 프런트엔드 공존 전체가 거의 비어 있다.** 한 줄 요약:

- **공통 결함(전 엔진)**: Query/Event/Security 모두 **추상화는 존재하지만 실제 경로가 그 위를 통과하지 않는다.** 비전의 "모든 X가 Y를 통과"가 세 엔진 모두에서 깨져 있다 — 추상화가 *사문화*되어 있거나(IQueryExecutor·IDriverManager 호출 0건), *죽은 코드*(KafkaMessageBus/Consumer 미등록)이거나, *분산된 표준 미들웨어*(Security)에 머문다.
- **프런트엔드 공존(문서 제목 그 자체)은 사실상 0%**: React/Vue/JS 자산 전무, JS interop 0건, 공존 셸·공유 계약 없음. 현 프런트는 순수 Blazor Server.
- **메타데이터 주도 화면(Low-Code 화면)도 0%**: 모든 화면이 손코딩 `@page .razor`.
- **가장 강한 재활용 자산**: NexusFramework `FlowExecutionEngine`(워크플로우 실행), `[WorkflowCallable]` 리플렉션 브리지, Blazor `MdiTabService`(MDI 셸), NexusCom 데이터/Kafka 드라이버, 보안 building block(JWT/PBKDF2/RateLimit/잠금).

### 성숙도 스코어카드

| 비전 필러 | 추상화 존재 | 실제 적용(경로 강제) | 종합 | 핵심 한 줄 |
|---|---|---|---|---|
| Low-Code (화면+로직) | △ 로직만 | ✕ 화면 0 | **~25%** | 워크플로우 실행 코어는 견고, 화면 생성·현업 디자이너는 전무 |
| Pro-Code (React/Vue) | ✕ | ✕ | **~15%** | REST/SignalR/CORS/JWT API 표면은 재활용 가능, 프런트 스택·공존 셸 0 |
| 단일 Screen Runtime | △ | ✕ | **~25%** | Blazor+MDI 셸이 근접 자산, 메타데이터 구동 화면 런타임 없음 |
| Query Engine | ✔ (NexusCom) | ✕ (우회) | **~35%** | 성숙한 추상화가 있으나 리포지토리가 `CreateConnection`으로 한 단계 아래에 직결 |
| Event Bus | ✔ (Kafka) | ✕ (죽은 코드·우회) | **~20%** | Kafka 글루 미등록, 실시간은 SignalR 직접 푸시로 버스 우회 |
| Security Engine | ✔ (building block) | △ (분산) | **~45%** | 강한 부품들이 표준 미들웨어에 분산, 게이트웨이형 PEP·권한 인가 없음 |
| Platform(NexusFramework) | △ | ✕ | **~25%** | 디자이너·엔진·콘솔이 존재하나 상호 단절, "NAF Studio" 통합 비전 미명문화 |

> 종합 환산: **비전 대비 약 20~25% 구현.** 다만 "재활용 가능한 building block"이 풍부해 — 신규 구축보다 **기존 자산을 게이트웨이로 승격·재배선**하는 작업이 1차 레버리지다.

---

## 2. 필러별 갭 분석

각 항목: **비전 / 현재 자산 / 핵심 갭 / 재활용 레버리지**.

### 2.1 Low-Code — 현업도 화면·로직 개발

> **용어 정정(중요)**: "Low-Code"는 **두 개의 별개 트랙**이며 혼동하면 안 된다.
> 1. **프론트엔드 Low-Code = 화면 디자이너** — 현업이 화면(폼/그리드)을 시각적으로 조립(ScreenDefinition 메타데이터 생성) → 화면 런타임이 렌더. *프론트엔드* 관심사. (Phase 3 메타모델 + Phase 4 디자이너)
> 2. **워크플로우 디자이너 = 비즈니스 로직** — 그래프로 업무 로직/오케스트레이션을 구성 → `FlowExecutionEngine`이 실행. *백엔드 로직* 관심사. (§8에서 앱 연계 완료, NexusFramework 그래프 에디터)
>
> 이 둘은 산출물(화면 메타 vs 워크플로우 그래프)·실행 주체(화면 런타임 vs FlowExecutionEngine)·계층(프론트 vs 백엔드)이 모두 다르다. 아래 자산/갭은 두 트랙을 구분해 읽어야 한다.

- **비전**: (프론트 트랙) 비주얼 화면 디자이너로 현업이 코딩 없이 화면을 구성. (로직 트랙) 워크플로우 그래프로 업무 로직 구성.
- **현재**: `FlowExecutionEngine`(DAG 검증·위상정렬·병렬 실행·Try/Catch/Finally) + `INode`/`NodeRegistry` + `[WorkflowCallable]`/`AssemblyInvocationNode`(DLL 메서드를 노드로 리플렉션 호출) = **로직 실행 코어는 성숙**. VS Code 확장 그래프 디자이너(workflow-editor.js ~1,920줄) 존재. §8로 `WorkflowController`가 `*.workflow` 실행까지 배선.
- **핵심 갭**:
  - 메타데이터 주도 **화면 생성 전무** — `FormDefinition`/`ScreenDefinition` 메타모델 없음, `DynamicComponent`/`RenderTreeBuilder` 미사용, 모든 화면 하드코딩.
  - 디자이너가 **VS Code 전용**(브라우저/현업 접근 불가, Blazor 미통합).
  - 콜러블 노드가 **데모 2개(Echo/Uppercase)**뿐 — MES 업무 노드(레시피 승인·트래킹·인터록) 라이브러리 부재.
  - **로직↔화면 바인딩 계층 없음**.
- **레버리지**: `[WorkflowCallable]`을 NexaMes 도메인 서비스에 부착 → 현업용 함수 팔레트 자동 생성. `MENU`/`UiId` 메타 인프라를 화면 정의 저장소로 확장.

### 2.2 Pro-Code — React/Vue 자유 개발
- **비전**: 외부 React/Vue 개발자가 Blazor 화면과 공존하며 표준 계약 위에서 SPA 개발.
- **현재**: `NexaOne.API` REST(api/v1/*, ~160 엔드포인트, string-enum), `/hubs/smartees` SignalR, JWT Bearer + CORS, Swagger(Dev 한정) = **외부 SPA가 소비할 백엔드 표면은 양호**.
- **핵심 갭**:
  - React/Vue/Angular/Vite/tsconfig/.tsx/.vue **0건**, JS interop(`IJSRuntime`/`JSInvokable`) **0건**, 마이크로프런트엔드/공존 셸 없음.
  - 외부와 공유할 **언어 중립 계약 산출물 부재**(DTO가 C# `ApiModels.cs`에만). TypeScript SDK/npm 패키지 없음.
  - Swagger **Dev 전용**, CORS `AllowedOrigins`가 `localhost:5000` 단일.
  - 공유 디자인 시스템/토큰 없음(UI가 DevExpress.Blazor 강결합).
- **레버리지**: 기존 REST/SignalR/JWT를 외부 SPA 1차 백엔드로 즉시 재사용. OpenAPI→TS 클라이언트 자동생성으로 계약 공유. App.razor 셸 + MdiTabService를 마이크로프런트엔드 마운트 지점으로 확장.

### 2.3 단일 Screen Runtime — 모든 화면이 동일 Runtime
- **비전**: 모든 화면이 단일 공통 실행/렌더 런타임 위에서 동작.
- **현재**: Blazor Server(InteractiveServer) 단일 렌더 파이프라인 + **`MdiTabService`(MDI 탭 셸: 탭 라이프사이클·중복정책 해시·LRU 20탭·SaveHandler·더티가드)** = 비전에 가장 근접한 실 자산. 별개 축으로 워크플로우 런타임(FlowExecutionEngine)·설비 런타임(PlantController) 존재.
- **핵심 갭**:
  - 화면이 전부 **정적 `@page` 컴파일 페이지(~41개)** — 메타데이터에서 해석해 렌더하는 화면 런타임 없음.
  - 공통 **화면 컨트랙트(`IScreen` Load/Validate/Save/Dispose) 부재** — SaveHandler 약한 규약만.
  - 화면/워크플로우/설비 런타임 **미수렴**. `NexaOne.Server`(Spring.NET ApplicationServer) ↔ Blazor Web 프로세스 분리.
- **레버리지**: `MdiTabService`를 화면 런타임 호스트로 확장(이미 라이프사이클 보유). `Routes.razor` 단일 Router + `MenuItem.UiId` + `MenuCacheService.FindByProgramIdAsync`를 `DynamicComponent` 기반 메타 라우팅으로 확장.

### 2.4 Query Engine — 모든 데이터가 통과
- **비전**: 모든 데이터 접근이 명명 쿼리 레지스트리를 갖춘 단일 Query Engine 게이트웨이를 통과.
- **현재**: NexusCom `IQueryExecutor`/`IDatabaseProvider`/`IDriverManager`(4 DBMS)·`INexaOneEESDbCapability`(방언) = **성숙한 추상화**. NexaMes는 `QueryRepository`/`ServiceObjectProcessor` 기반 클래스로 44개 리포지토리 일원화, UI 직접 DB 접근 **0건**.
- **핵심 갭**:
  - **게이트웨이 우회**: `IQueryExecutor`/`IDriverManager` NexaMes 호출 **0건**. 리포지토리가 한 단계 아래 `CreateConnection`+Dapper로 직결.
  - **명명 쿼리 레지스트리 부재**: SQL이 44개 리포지토리에 `const string` 인라인.
  - **공급자 중립성 미달**: `WITH(NOLOCK)` 109건 하드코딩(방언 추상화 있음에도 미사용).
  - **백도어**: `RuleController` `/api/v1/query`가 임의 SQL 직접 실행.
- **레버리지**: `QueryRepository`/`ServiceObjectProcessor` **내부만** `IDriverManager`로 위임 교체 → 44개 리포지토리·전 화면 무변경으로 게이트웨이 강제. `INexaOneEESDbCapability`로 NOLOCK/페이징 치환. const SQL을 (모듈,쿼리명) 카탈로그로 점진 이관.

### 2.5 Event Bus — 모든 이벤트가 통과
- **비전**: 모든 도메인/실시간 이벤트가 단일 Event Bus(Kafka)를 통과.
- **현재**: NexusCom `KafkaDriver`(성숙) + NexaMes `KafkaMessageBus`/`KafkaConsumerService`(설계 성숙) + SignalR `IEesHubNotifier`(실가동). `ChangeEventDispatcher`(CDC).
- **핵심 갭**:
  - Kafka 글루가 **DI/HostedService 미등록 = 죽은 코드**(실행 경로에 없음).
  - 실시간 이벤트가 **버스 우회** — 컨트롤러/HostedService가 `IEesHubNotifier`를 직접 호출해 SignalR 즉시 푸시(Kafka 미경유).
  - **이벤트 추상화 부재**(`IEventBus`/`IEventDispatcher`/MediatR 등 0건).
  - **3중 단절 이벤트 체계**: NexaMes `IDomainEvent`(골격만) / NexusFramework `IExecutionEvent` / NexusCom `ChangeEvent`가 각자 독립.
  - 원자성 부재(transactional outbox 없음).
- **레버리지**: `KafkaDriver` 백본 재사용. `IEesHubNotifier`를 "버스 구독자"로 재배치(컨트롤러 직접호출 제거 → Consumer 핸들러가 Notifier 호출). `AggregateRoot.RaiseDomainEvent` 골격 + SaveChanges 인터셉터(outbox) → Kafka 발행. `DomainEventMessage`를 공통 봉투로 3체계 어댑팅.

### 2.6 Security Engine — 모든 권한이 통과
- **비전**: 모든 인증·인가가 단일 게이트웨이형 정책 집행점(PEP)을 통과.
- **현재**: JWT(HMAC, 약한키 부팅거부) + PBKDF2 해시 + RateLimiting + 계정잠금 + 미들웨어(PasswordChangeRequired/RequestLogContext) + Role/User 도메인 = **강한 building block**.
- **핵심 갭**:
  - **게이트웨이형 Security Engine 부재** — 표준 미들웨어 + 컨트롤러 `[Authorize]` 분산, 커스텀 정책/핸들러 0건.
  - **`Role.Permissions` 정의·영속화되나 인가 미사용** — 실제는 하드코딩 역할명(`"ADMIN"`/`"OPERATOR"`) 체크. 권한 클레임 미발급.
  - 단일 역할(User당 1 RoleId), 다중역할/계층 없음.
  - RefreshToken 인메모리(분산 미지원, JTI blacklist 없어 즉시 무효화 불가).
  - 외부 IdP(LDAP/AD/OIDC) 미통합(`LdapDriver` 존재하나 미연결). 대칭키(JWKS 없음).
- **레버리지**: `Role.Permissions` → permission 클레임 발급 + 커스텀 `IAuthorizationPolicyProvider` → `[Authorize(Policy=...)]`로 PEP화. 인가 미들웨어를 PasswordChangeRequiredMiddleware 패턴으로 추가. `LdapDriver`를 ValidateAndLogin 뒤 외부 인증 어댑터로 연결.

### 2.7 Platform (NexusFramework "NAF Studio")
- **현황**: "NAF Studio" 통합 비전은 **코드/문서에 미명문화**("NAF"는 Android 패키지 식별자로만 등장). 실재는 (1) 그래프 워크플로우 디자이너(VS Code·VS, **VSIX 빌드 실패 P0**), (2) `NexusOne.UI` MAUI Blazor 운영콘솔(**목업 데이터**), (3) RULE/FDC 도메인 엔진(이벤트 버스 없음), (4) Spring.NET 컨텍스트.
- **핵심 갭**: 디자이너↔FlowExecutionEngine **실행 단절**(저장 그래프를 엔진이 직접 로드·실행·캔버스 반영하는 경로 미구현), 네이밍 불일치(README SForge.* vs 실제 NexusFramework.*), 테스트/CI 미비.
- **레버리지**: `FlowExecutionEngine`(최고 성숙) 라이브러리 임베드, `media` 그래프 캔버스 에셋을 Blazor에 임베드, `NexusOne.UI` Razor 셸 재사용.

---

## 3. 횡단 핵심 통찰

1. **"엔진 = 게이트웨이" 원칙이 어디에도 강제되지 않는다.** Query/Event/Security 세 엔진 모두 추상화는 있으나 실제 경로가 통과하지 않는다(우회·죽은코드·분산). 비전의 정체성("모든 X가 Y를 통과")을 살리려면 **신규 엔진 구축이 아니라, 기존 단일 지점(QueryRepository/Notifier/미들웨어)을 게이트로 승격**하는 것이 정공법이며 비용 대비 효과가 가장 크다.
2. **프런트엔드 공존(문서 제목)이 가장 비어 있다.** React/Vue·JS interop·공존 셸·공유 계약 전부 0. 그러나 백엔드 계약(REST/SignalR/JWT)은 양호 → SPA를 "붙일" 토대는 있다.
3. **자산은 풍부하나 배선이 끊겨 있다.** 죽은 코드(Kafka 글루)·미사용 추상화(IQueryExecutor·Role.Permissions·LdapDriver)·미통합 디자이너가 많다 — "구현"보다 "연결/승격"이 1차 작업.

---

## 4. 단계별 로드맵

원칙: **저비용·고레버리지(기존 자산 승격) → 신규 프런트 스택(고비용)** 순. 각 단계는 독립적으로 가치를 낸다.

### Phase 0 — 정렬 & 결정 (S, 선행)
- 비전·네이밍 확정("NAF Studio" 정의 또는 폐기), 대상 범위(어느 화면이 Low-Code 대상인지) 결정.
- **엔진 게이트웨이 계약 3종 설계**: `IQueryGateway`(명명쿼리), `IEventBus`, `IAuthorizationPolicy(permission)`.
- 산출물: 본 문서 + ADR 3건. **의존성: 없음.**

### Phase 1 — 엔진을 게이트웨이로 승격 (M, 최고 레버리지, 프런트 무관)
1. **Query Engine**: `QueryRepository`/`ServiceObjectProcessor` 내부를 `IDriverManager`/`IQueryExecutor` 위임으로 교체(리포지토리 시그니처 불변) → 전 화면 자동 통과. 명명 쿼리 카탈로그 도입 + const SQL 점진 이관. `INexaOneEESDbCapability`로 NOLOCK/페이징 치환. `RuleController /query` 백도어를 게이트 경유로 통합 또는 폐쇄.
2. **Event Bus**: `KafkaMessageBus`/`KafkaConsumerService` DI·HostedService 등록(죽은 코드 활성화). `AggregateRoot.RaiseDomainEvent` + SaveChanges outbox 인터셉터 → Kafka 발행. `IEesHubNotifier`를 Consumer 핸들러의 구독자로 재배치(컨트롤러 직접 호출 제거). `DomainEventMessage` 공통 봉투로 수렴.
3. **Security Engine**: permission 클레임 발급 + 커스텀 `IAuthorizationPolicyProvider` → `[Authorize(Policy="perm:...")]`로 하드코딩 역할명 대체. 인가/감사 PEP 미들웨어 추가. RefreshToken 분산 저장(Redis) 옵션.
- **효과: 비전의 3대 "통과" 원칙을 신규 UI 없이 달성.** 의존성: Phase 0.

### Phase 2 — Pro-Code 공존 표면 (M)
- OpenAPI 운영 노출 + **TypeScript SDK/타입 자동생성**(npm 패키지화) → C# DTO를 외부 프런트와 공유.
- CORS **다중 오리진 화이트리스트**, SignalR(@microsoft/signalr) 외부 클라이언트 가이드.
- **JS interop 마운트 지점** + 마이크로프런트엔드 로더(App.razor 셸에 React/Vue 위젯 임베드 PoC).
- 공유 디자인 토큰(최소 CSS 변수) 도출.
- **효과: 외부 React/Vue 개발자가 동일 백엔드로 SPA 개발 가능.** 의존성: Phase 1(보안/이벤트 계약 안정).

### Phase 3 — 단일 Screen Runtime + 메타데이터 화면 (L)
- 공통 **`IScreen` 컨트랙트**(Load/Validate/Save/Dispose) 정의, `MdiTabService`를 화면 런타임 호스트로 승격.
- `MenuItem.UiId` → `ScreenDefinition` 메타 저장소 + `DynamicComponent` 메타 라우팅.
- `FormDefinition`/`FieldDefinition`/`GridColumnDefinition` 메타모델 + 런타임 폼/그리드 렌더러(DevExpress 래핑).
- **효과: 메타데이터로 정의한 화면이 동일 런타임에서 렌더.** 의존성: Phase 1(Query/Event), Phase 2(계약).

### Phase 4 — 브라우저 Low-Code (L)
- VS Code `workflow-editor.js` 그래프 캔버스를 **Blazor/웹 임베드**(또는 React Flow 정식 도입)로 포팅, `WorkflowController` 실행 파이프라인과 연결.
- **MES 업무 노드 라이브러리**: 도메인 서비스에 `[WorkflowCallable]` 부착(레시피 승인·트래킹·인터록…) → 현업 함수 팔레트.
- **로직↔화면 바인딩**: 화면 이벤트 → 워크플로우 실행, 결과 → 화면 갱신.
- **효과: 현업이 브라우저에서 화면+로직 조립.** 의존성: Phase 3 + NexusFramework 디자이너 정비.

### Phase 5 — 플랫폼 통합 (L, 선택)
- NexusFramework 디자이너↔`FlowExecutionEngine` 실행 폐루프, VSIX P0 해결.
- `NexusOne.UI` 운영콘솔 실데이터 바인딩 → NexaMes 운영 셸.
- "NAF Studio" 단일 셸(Low-Code 디자이너 + Pro-Code 위젯 + 운영콘솔)로 수렴.
- **효과: 단일 플랫폼 통합 운영.** 의존성: Phase 1~4.

### 우선순위 요약
| 단계 | 비용 | 레버리지 | 프런트 신규 | 권장 |
|---|---|---|---|---|
| Phase 1 (엔진 승격) | M | ★★★ | 없음 | **즉시 — 비전 정체성 달성, 위험 낮음** |
| Phase 2 (Pro-Code) | M | ★★ | TS SDK | 외부 프런트 수요 시 |
| Phase 3 (화면 런타임) | L | ★★ | 메타 렌더러 | Low-Code 본격화 전제 |
| Phase 4 (Low-Code) | L | ★★★ | 디자이너 | 현업 저작 목표 시 |
| Phase 5 (통합) | L | ★ | 셸 | 장기 |

---

## 5. 위험 & 선행 결정 사항

- **결정 1 — 프런트 전략**: Blazor 유지 + React/Vue 공존(마이크로프런트엔드) vs 점진 전환? Phase 2~4 방향을 좌우.
- **결정 2 — "NAF Studio" 정체성**: NexusFramework 디자이너/콘솔을 제품화할지, NexaMes에 임베드만 할지.
- **위험**: NexusFramework 디자이너 VSIX 빌드 실패(P0)·테스트 부재 → Phase 4/5 선결 과제. README↔코드 네이밍 불일치로 비전 신뢰도 저하 → Phase 0에서 정리.
- **위험**: Query 게이트 승격 시 44개 리포지토리 회귀 — 단, 진입점이 `QueryRepository` 단일이라 내부 교체로 한정 가능(테스트로 가드).

---

## 6. 결론

비전은 **"새 플랫폼을 짓는" 문제가 아니라 "흩어진 자산을 게이트웨이로 연결·승격하는" 문제**에 가깝다. **Phase 1(엔진 승격)이 최소 비용으로 비전의 3대 정체성("모든 X가 Y를 통과")을 달성**하며 프런트엔드 변경이 전혀 없어 가장 먼저 권장된다. 프런트엔드 공존(Pro-Code/Low-Code)은 백엔드 계약이 양호해 "붙일" 토대는 있으나 실질 신규 구축(TS SDK·메타 렌더러·브라우저 디자이너)이 필요하다.

---

## 7. 진행 현황 (2026-06-13)

| Phase | 상태 | 비고 |
|---|---|---|
| **0** ADR | ✅ 완료 | ADR-001/002/003 (`docs/design/adr/`) |
| **1** 엔진 승격 | ✅ 완료 | Query Gateway(`IQueryGateway`)·Event Bus(outbox+IMessageBus)·Security PEP(permission 정책 전 컨트롤러 적용) |
| **2** Pro-Code | ✅ 토대 | React+Vite+TS SPA(`src/01.Web/NexaOne.Spa`)+NSwag+permission 가드+토큰갱신. ⚠️ npm 미실행(개발자 빌드 검증 필요). 잔여: 생성 클라이언트 적용·Blazor 셸 임베드 |
| **3** 화면 런타임 | ✅ 토대 | `ScreenDefinition` 메타모델+`IScreen`+`MetaScreen`(/meta/{UiId}) 동적 렌더 |
| **4** Low-Code 화면 디자이너 | ✅ 토대 | `ScreenDesigner`(/designer/screen)+DB 저장소(SYS_SCREEN_DEFINITION). 잔여: 그리드 런타임 렌더러·저장 명령 바인딩 |
| **5** 플랫폼 통합 | ⏸ 보류 | **현 시점 NexaMes 범위 밖.** ① NexusOne.UI는 NexusFramework 서브모듈의 MAUI 콘솔(목업)로 별개 제품·크로스레포 ② 디자이너↔엔진 폐루프는 사용자 결정(IDE 저작)으로 보류 ③ "NAF Studio 셸"은 미정의 개념. 추진하려면 별도 제품 결정·서브모듈 작업 필요 |

> **용어**: §2.1의 두 트랙 구분 참고 — **프론트 Low-Code(화면 디자이너, Phase 3·4)** ≠ **워크플로우 디자이너(비즈니스 로직, §8 FlowExecutionEngine)**. 후자는 사용자 결정으로 "개발자 IDE 저작 + NexaMes 실행" 범위로 확정(브라우저화 보류).

# Frontend Coexistence Architecture

## 개요

NexaMes 프론트엔드는 **Low-Code 플랫폼과 일반 프론트엔드 개발 환경이 공존**할 수 있도록 설계한다. 두 개발 방식(현업의 시각적 화면 조립, 개발자의 자유 코딩)이 서로를 막지 않으면서, 만들어진 모든 화면은 동일한 플랫폼 코어(Runtime · Query Engine · Security · Event Bus) 위에서 동작한다. 이로써 화면이 어떻게 만들어졌든 데이터 접근·권한·이벤트·감사는 단일 경로로 통제된다.

## 세 갈래 트랙

### Low-Code 트랙 — 현업도 개발 가능

현업이 위젯을 캔버스에 드래그&드롭으로 배치해 MES 화면을 시각적으로 조립한다.

- **시각 에디터**: React SPA(NexaOne.Spa)에 호스팅한 **GrapesJS 기반 WYSIWYG 디자이너**. 끌어다 놓는 컴포넌트는 자유형 HTML이 아니라 **플랫폼 프리미티브**(명명 쿼리에 바인딩된 데이터 그리드, 저장 쿼리에 바인딩된 폼/필드, 명령 버튼 등)다.
- **산출물**: GrapesJS 사설 HTML이 아니라 **렌더러 중립적 레이아웃 트리**(`ScreenDefinition.Layout`). `SYS_SCREEN_DEFINITION.DEFINITION_JSON`에 불투명 저장된다.
- **런타임**: Blazor `/meta/{UiId}`가 그 레이아웃대로 렌더(기존 `MetaGridRenderer`/`MetaFormRenderer` 재사용). 디자인된 화면이 기존 화면·메뉴·MDI 셸 안에 그대로 위치한다.
- 상세 설계: [GrapesJS 화면 디자이너 설계](specs/2026-06-19-grapesjs-screen-designer-design.md).

기존 정의-폼 `/designer`(필드/컬럼 입력)는 유지되며, GrapesJS 디자이너는 이를 **대체가 아니라 병행 추가**한다. 좌표·레이아웃이 없는 레거시 화면은 런타임의 평면 경로로 계속 렌더되어 완전 하위호환된다.

### Pro-Code 트랙 — React/Vue 개발자도 자유롭게 개발 가능

표준 SPA 개발 흐름을 그대로 쓴다.

- React SPA(NexaOne.Spa, Vite + TypeScript)가 `/spa` 경로에 임베드되어 Blazor 셸과 공존한다.
- **타입 안전 API 클라이언트**: 백엔드 OpenAPI에서 NSwag로 생성(`gen:api`). 컨트롤러의 `[ProducesResponseType<T>]`/`[ProducesErrorResponseType(typeof(Error))]`가 응답·오류 계약을 보장.
- 인증은 JWT(`apiFetch` 래퍼가 Bearer 부착, 401 시 단일-flight 리프레시), 실시간은 SignalR 허브.
- 개발자는 손코딩 화면을 자유롭게 작성하되, 데이터·권한은 아래 플랫폼 코어를 통과한다.

### Platform 코어 — 단일 플랫폼으로 통합 운영

화면이 어떤 트랙으로 만들어졌든 다음을 **반드시** 통과한다.

- **Runtime**: 모든 메타데이터 화면이 동일 런타임(`/meta`) 위에서 동작.
- **Query Engine**: 모든 데이터 접근이 명명 쿼리(`/api/v1/query`·`/api/v1/command`, 파일 기반 레지스트리)를 통과 — 임의 SQL은 admin 전용.
- **Security Engine**: 모든 권한이 PEP(ADR-003, `perm:module:action`)를 통과 — 서버가 유일 경계, 클라이언트 권한 체크는 UX 힌트.
- **Event Bus**: 모든 이벤트가 도메인 이벤트→아웃박스 트랜잭션(ADR-002)을 통과 — 데이터 변경과 이벤트가 동일 트랜잭션에서 원자적으로 커밋.
- **감사**: `@currentUser/@utcNow`는 서버 주입(클라이언트 위조 불가).

## 설계 원칙

- **단일 진리원천**: 화면 정의 구조의 권위는 C# `ScreenDefinition` 계약. SPA는 생성된 타입 형태를 타깃(병렬 손작성 직렬화기 금지).
- **하위호환 우선**: 모든 확장은 기존 평면 정의·런타임을 깨지 않게 추가만 한다.
- **플랫폼 통과 불변식**: 어떤 트랙도 Query/Security/Event 코어를 우회할 수 없다.

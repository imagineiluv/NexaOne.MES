---
name: frontend-design
description: NexaOne MES 프론트엔드 디자인 작업 시 사용 — 화면/CSS/스타일/다크모드/UX 개선, 새 화면·컴포넌트 추가, Radzen 커스터마이징. 디자인 토큰 규칙·클래스 인벤토리·검증 워크플로·기수(旣修) 함정을 담는다.
---

# NexaOne 프론트엔드 디자인 스킬

Blazor Server 통합 호스트(NexaOne.Server) + RCL(NexaOne.Web.Components) + Radzen Blazor 11.x.
스타일 단일 출처: `src/00.Main/NexaOne.Server/wwwroot/css/nexaone.css` (--nx-* 토큰).

## DESIGN.md 업무 원칙

화면의 정보 계층·컴포넌트 선택·관리 화면 View 모드 기준은 Obsidian Wiki의
`docs/design/NexaOne-DESIGN.md`를 함께 확인한다. getdesign.md의 Discord 분석에서는
짙은 배경의 표면 계층, 강한 활성 상태, 8px 간격 리듬, 44px 터치 목표만 원칙으로 채택한다.
Blurple·Magenta·과장된 대문자/그라데이션을 복제하지 않고 NexaOne 네이비·틸과 시맨틱 상태색을 유지한다.

CSS·토큰의 기술적 단일 출처는 계속 이 스킬과 `nexaone.css`이며, Wiki는 업무 UX 의사결정의 단일 출처다.

## 절대 규칙 (위반 = 리뷰 반려 수준)

1. **인라인 `style=` 금지.** 전 스타일은 nexaone.css의 토큰 기반 클래스로. 동적 상태는 클래스 토글(`@(cond ? "is-disabled" : "")`).
   **유일한 예외 = 런타임 계산 값**(컨텍스트 메뉴 좌표 `left/top`, 트리 깊이 패딩 `padding-left:@(Pad)px`, 사이드바 폭 CSS 변수, 레이아웃 span `flex-basis`) — 정적 색·폰트·간격은 예외 불가.
2. **콘텐츠 영역에 hex 색 금지.** `--nx-*` 토큰만 — 인라인/하드코딩 hex는 `[data-theme="dark"]`에서 안 뒤집혀 다크 파손(흰 띠·암전 텍스트)을 만든다. 예외: 사이드바(항상 네이비 고정)의 `#2a3c5c` 계열, 로고 마크.
3. **타입 램프 6단 밖 font-size 금지**: `--nx-fs-title`(20) / 14(강조) / `--nx-fs-section`·`--nx-fs-body`(13) / `--nx-fs-dense`(12.5, 그리드) / `--nx-fs-caption`(12) / `--nx-fs-caption-s`(11). 예외: 로고 태그(7.5/9), KPI 대형 수치(36), 아이콘 글리프.
4. **radius 3단**: `--nx-radius-sm`(4, 인풋·칩) / `--nx-radius`(6) / `--nx-radius-lg`(10) + 999px(필)·50%(원). **그림자 3단**: `--nx-shadow-sm/md/lg`.
5. **Radzen 색·표면은 브리지로만.** nexaone.css 상단 `--rz-* → --nx-*` 브리지 블록에 추가(개별 규칙 오버라이드보다 우선 검토). 다크가 자동으로 따라온다.
6. **다크 모드 = 토큰 재정의로만** (`[data-theme="dark"]` 블록). 신규 표면색 추가 시 다크 값도 반드시 함께.

## 토큰 치트시트 (nexaone.css :root)

- 표면: `--nx-bg`(페이지) `--nx-card`(카드) `--nx-head`(표 헤더) `--nx-stripe`(줄무늬) `--nx-hover` `--nx-hover-teal` `--nx-selected`
- 텍스트: `--nx-text` `--nx-text-2`(보조) `--nx-muted`(캡션) / 보더: `--nx-border` `--nx-border-soft`
- 브랜드: `--nx-teal`(주색) `--nx-teal-d`(호버/링크) `--nx-navy`(사이드바) `--nx-blue`
- 시맨틱: `--nx-success|warning|danger|info` + 각 `-bg`(틴트 — 다크 값 별도 정의됨)
- 간격: `--nx-sp-1..6` (4/8/12/16/24/32)

## 클래스 인벤토리 (신규 CSS 전에 기존 것 먼저)

- **셸**: `mes-card`+`mes-card-head`+`mes-card-body` / `mes-empty-state`(es-title/es-sub) / `page-header`+`page-title`
- **셸 내부 손코딩 페이지**(host-*): `host-page`(래퍼) `host-desc` `host-toolbar` `host-input.inline` `host-btn.inline` `host-btn-secondary` `host-table`(+`td.empty`) `host-chip.read|write|ok|bad` `host-actionlink` `host-sep` `is-disabled`
- **인증 카드**(로그인/가입/비번): `nexa-host-body`(플렉스 센터) `host-card` `host-brand` `host-title` `host-sub` `host-field` `host-input` `host-btn` `host-error` `host-help` `host-link`
- **메타 엔진**: `meta-grid`(+툴바/필터/페이저) `meta-form`·`meta-field` `meta-search` / 상태: `nx-skeleton` `nx-grid-empty` `nx-grid-trunc` / 그리드 개인화: `nx-colmenu*` `nx-bulkbar` `nx-density-roomy` `nx-freeze-first`

## 기수 함정 (전부 실제로 겪은 것 — 재발 금지)

- **Radzen 줄무늬는 td 레벨** `--rz-grid-stripe-background-color`(기본 `--rz-base-50` #fafafa 고정). tr 배경으로는 못 덮는다 → 브리지에서 `--rz-base-50/100/200/300` 전 스케일을 --nx로 매핑해 해결(이미 적용). Radzen 신규 컴포넌트 도입 시 material-base.css에서 사용하는 `--rz-*` 변수를 확인해 브리지에 추가.
- **FocusOnNavigate(Selector="h1")** 가 내비마다 h1을 포커스한다 → 전역 `h1:focus { outline:none }` 적용됨. 새 페이지 제목은 `<h1 class="page-title">`로(인라인 스타일 h1 금지).
- **`.host-card`류 마진 붕괴**: 부모가 플렉스가 아니면 자식 margin이 부모 밖으로 새 배경 밴드가 노출된다. 인증 카드는 `nexa-host-body`의 플렉스 센터가 소유.
- **LayoutRenderer는 재귀 시 파라미터 자동 전파 안 됨**: 새 `[Parameter]` 추가 시 `RenderChildren`의 `builder.AddAttribute(...)` 목록에도 반드시 추가(누락 시 깊은 위젯에서 조용히 null).
- **Radzen 동적 추가 컬럼은 끝(우측)에 붙는다**: 조건부 컬럼(체크박스 등)을 좌측에 두려면 그리드에 `@key`를 줘 재생성.
- **Razor에서 리터럴 중괄호**: `{'{'}` 이스케이프는 화면에 그대로 노출된다 → `@("{TEXT}")` 사용.
- **레거시 잔재 주의**: 옛 수제 `<table>` 그리드용 CSS/JS는 제거됨 — `.col-resize`·`tr.selected`·`nxGridFocusMove` 등을 되살리지 말 것.

## 빌드·검증 워크플로

1. **CSS만 수정** → dev에서 소스 wwwroot 직접 서빙이라 **빌드 불필요**(브라우저 새로고침). **.razor 수정** → `dotnet build src/00.Main/NexaOne.Server/NexaOne.Server.csproj` 필요.
2. DLL 잠금(MSB3021/3027) 시: NexaOne.Server를 커맨드라인에 포함한 dotnet.exe 프로세스 종료 후 재빌드.
3. 부팅: 스크래치패드의 `boot-visual.sh`(SQLite, localhost:8080, admin/admin — dev 시드). 준비 신호 "Now listening on".
4. **시각 검증은 필수, 라이트+다크 쌍으로**: puppeteer로 로그인 → 대상 화면 → 스크린샷. 다크 전환은 `window.nxSetTheme('dark')`. 그리드 화면은 줄무늬/호버/선택행, 손코딩 페이지는 표 헤더·텍스트 대비를 확인.
5. UI 회귀 가드: bUnit(test/NexaOne.UnitTests/Web/*) — 컴포넌트 로직 변경 시 렌더 테스트 추가.

## 화면 유형별 지침

- **새 업무 화면**: 가능하면 메타 경로(ScreenDefinition 등록, InMemoryScreenDefinitionProvider) — 손코딩 페이지는 HTTP 표면/특수 UI만.
- **손코딩 페이지 신설 시**: `host-page` 래퍼 + `page-title` + `host-desc` + `host-table`/`mes-card` 조합. HostLogin.razor(인증)·전환 완료된 HostDashboardLayoutEdit.razor(셸 내부)가 모범.
- **밀도**: 그리드 셀 5px 상하(조밀 기본, `nx-density-roomy` 토글), 폼 입력은 body 13px. 캡션·보조엔 caption 계열.

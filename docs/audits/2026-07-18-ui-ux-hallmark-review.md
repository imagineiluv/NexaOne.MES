# 2026-07-18 UI/UX · Hallmark 최종 검토

- 최종 재검사: **2026-07-22**

## 범위

- Host 로그인과 MES Workbench
- Meta 화면·폼·그리드·컬렉션·레이아웃 위젯
- Designer Index와 ScreenEditor/GrapesJS iframe
- Mobile/PDA와 POP 작업 채널
- light/dark, 키보드, focus/hover/active/disabled, reduced-motion

설계 계약은 루트 `design.md`와 `tokens.css`를 기준으로 삼았다. Host, Portal, Razor Class Library, Designer iframe이 같은 색상·간격·타이포·터치 타깃 토큰을 사용한다.

## Hallmark 결과

58개 게이트를 최신 worktree에서 독립 재검사했다.

- Pre-emit self-critique: **P5 H5 E5 S5 R5 V5**

- PASS: **47**
- N/A: **11**
- FAIL: **0**

PASS 게이트: 1–7, 9–16, 19–27, 29–30, 33–42(38a 포함), 44–49, 51, 54–56.

N/A 게이트: 8·32(현재 빌드 기록만 있고 비교할 이전 Hallmark 산출물 없음), 17(custom tooltip timing 없음), 18(auto-rotate 없음), 28(video 없음), 31(Lottie 없음), 43(footer 없음), 50(image-bearing grid 없음), 52(theme section-head override 없음), 53(CSS radio tabs 없음), 57(study 요청 아님).

최종 정적 회귀에서 fine-pointer media 밖 hover 0건, `100vw` 0건, 토큰 밖 raw color/font 0건, 4pt spacing 이탈 0건을 확인했다. 중첩 카드, tiny eyebrow/pill 제목 패턴, 저대비 아이콘/KPI, 이중 `top: 0` sticky, 불완전한 interactive state도 해소했다.

### 발견사항

- Critical — 없음
- Major — 없음
- Minor — 없음

Summary — **0 critical · 0 major · 0 minor**
Verdict — **출고 가능; Hallmark slop tell 미검출**

## 브라우저 실측

| 영역 | 폭 | 결과 |
|---|---|---|
| Host Meta/Dashboard | 320, 375, 414, 768, 1280, 1440px | 문서 가로 넘침 0px, 모바일 44px 미달 컨트롤 0개 |
| Meta 컬럼 필터 | 320, 1280px | 오버레이 288px/480px, 320px 컨트롤 10개 모두 44px, 문서 가로 넘침 0px |
| Mobile | 320, 375, 414px | 문서 가로 넘침 0px, 44px 미달 컨트롤 0개 |
| POP | 768, 1280, 1440px | 문서 가로 넘침 0px, 56px 미달 컨트롤 0개 |
| Designer Index/Editor | 320, 375, 414, 768, 1280, 1440px | 문서 가로 넘침 0px, 44px 미달 컨트롤 0개; 모바일 뒤로가기 44×44px |

Designer는 `Ctrl+K` 검색 포커스와 편집기 iframe 준비 상태를 확인했다. iframe의 `--color-paper` 값은 공용 토큰에서 주입된 `oklch(95.7% 0.006 255.5)`였다. light/dark Host·Designer·POP도 별도 렌더링했고 가로 넘침은 없었다.

대표 캡처:

- `output/playwright/final-meta-filter-fixed-1280.png`
- `output/playwright/final-meta-filter-fixed-320.png`
- `output/playwright/final-designer-320.png`
- `output/playwright/login-final-1280.png`
- `output/playwright/host-grid-320.png`
- `output/playwright/host-pop-1280.png`
- `output/playwright/designer-home-1440.png`
- `output/playwright/designer-editor-375.png`
- `output/playwright/dark-host-grid.png`
- `output/playwright/dark-host-pop.png`

## 자동 검증

- Portal Vitest: **116/116 통과**
- Portal production build: 성공
- ScreenEditor lazy chunk: **1,036.63 kB raw / 286.23 kB gzip**, 300 KiB gzip 예산 통과
- NexaOne.Server Release build: **경고 0 / 오류 0**
- 전체 기준선: Unit **1,470/1,470**, Server **672/672**, NexaFramework **34/34** 통과
- PLC Integration **10/10**, Hardware simulation **27/27** 통과
- Kafka runtime factory/dispatcher **3/3** 통과

## 코드 리뷰 — 고정 기준점 대비

- 기준점: `8e9708fe28da5ab6b44e261fd31c41fa7a18c185` (현재 HEAD와 동일, 231개 tracked WIP 파일 diff)
- Standards: 문서화된 `CONTEXT.md` 용어·모듈 경계와 Fowler smell baseline 위반 **0건**
- Spec: 우선순위 계획과 전체 UI/UX·레이아웃·컴포넌트 고도화 요구의 누락·오구현·범위 이탈 **0건**
- 경계 확인: QMS host proxy SQL 0건, FDC/QMS/EST Application의 own Infrastructure/PLC import 0건, 신규 런타임 placeholder 0건

독립 Standards/Spec reviewer를 병렬 기동했으나 사용량 한도로 결과를 받지 못해, 같은 두 축을 고정 기준점에서 로컬 재검사했다. 컴파일·전체 테스트·architecture guard와 정적 diff 검사를 근거로 삼았다.

## 남은 외부 환경 증거

UI/UX와 로컬 자동 검증 범위에는 남은 실패가 없다. 다만 MSSQL 실DB migration/QMS 원자성, Kafka broker→SignalR, 실제 PLC/OPC-UA, 스케줄 워커 실효과는 현재 로컬에 Docker·SQL Server·broker·장비가 없어 실행하지 않았다. 이 항목은 `Environment-Verification.md`와 우선순위 실행 계획에서 미완료로 유지한다.

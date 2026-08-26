# NexaOne Portal client

NexaOne.Server가 `/Designer` 경로로 제공하는 React + Vite + TypeScript 화면 디자이너다.
REST API, SignalR, JWT/permission 계약은 통합 Server와 공유한다.

## 소유 및 배포 구조

| 경로 | 역할 |
|---|---|
| `ClientApps/Portal` | Portal 원본 소스와 Node 빌드 설정 |
| `wwwroot/spa` | Vite가 생성하는 배포 산출물 |
| `/Designer`, `/Designer/{uiId}` | 디자이너 로그인·화면 목록·화면 편집 정식 URL |
| `/spa/*` | 기존 북마크 호환용 레거시 URL과 Vite 배포 자산 기준 경로 |

Portal 소스는 Server 프로젝트가 소유하지만 브라우저에서 실행된다. Server의 C# 코드와
React 코드가 같은 프로세스에서 실행되는 구조는 아니다.

## 개발 실행

저장소 루트에서 Server 번들 빌드, SQLite Server, Vite 개발 서버를 한 번에 실행한다.

~~~powershell
powershell -ExecutionPolicy Bypass -File tools/run-dev.ps1
~~~

개별 실행 시 전제 조건:

- 실행 중인 NexaOne.Server (`http://localhost:5173`)
- Node.js 20 이상
- npm

~~~bash
cd src/00.Main/NexaOne.Server/ClientApps/Portal
npm ci
copy .env.example .env
npm run dev
~~~

사용자 통합 URL은 `http://localhost:5173`이고, Vite HMR URL은 `http://localhost:5174`이다. Vite는 `/api`와 `/hubs`를
`VITE_API_PROXY`에 지정한 Server 주소로 프록시한다. 주요 진입점은 다음과 같다.

| URL | 역할 |
|---|---|
| `/` | MES 로그인으로 이동한 뒤 Blazor MES 셸 진입 |
| `/Designer` | 디자이너 전용 로그인과 DB 화면 목록 |
| `/Designer/{uiId}` | GrapesJS 화면 편집 |
| `/Mobile`, `/Mobile/{uiId}` | PDA·모바일 작업 화면 목록과 런타임 |
| `/POP`, `/POP/{uiId}` | 키오스크·POP 작업 화면 목록과 런타임 |

디자이너가 저장하는 `TARGET_CHANNEL`과 `ENTRY_PATH`는 `SYS_SCREEN_TARGET`에 기록된다.
채널은 `MES | MOBILE | POP`, 경로는 각각 `/meta/{uiId}`, `/Mobile/{uiId}`, `/POP/{uiId}`다.

구조·실행·검증의 Wiki 원본은 `docs/design/Portal-Client-Structure.md`(Obsidian 볼트)다.

## 검증 및 운영 빌드

~~~bash
npm test
npm run build
~~~

`npm run build`는 별도 `dist`를 만들지 않고 Server의 `wwwroot/spa`를 갱신한다.
`dotnet publish`는 Server 프로젝트의 `BuildPortalClientBundle` 타깃을 통해 의존성을
필요한 경우 설치하고 Portal을 자동 빌드한다.

## 환경 설정

| 변수 | 의미 |
|---|---|
| `VITE_API_PROXY` | 독립 Vite 개발 서버가 전달할 NexaOne.Server 주소 |
| `VITE_API_BASE_URL` | 별도 오리진 배포 시 사용할 API 기준 URL. 같은 Server 배포에서는 비워 둔다. |

## 주요 소스 구조

| 경로 | 역할 |
|---|---|
| `src/App.tsx` | 라우트와 세션 조립 |
| `src/pages` | 디자이너 로그인, 화면 목록·생성, 화면 편집 라우트 페이지 |
| `src/designer` | GrapesJS 디자이너 도메인 로직과 계약 테스트 |
| `src/api` | JWT Bearer API 클라이언트와 인증 계약 |
| `src/auth` | JWT permission 해석 |
| `src/realtime` | SignalR 연결 |
| `src/components` | 공통 React 컴포넌트 |
| `src/styles` | Portal 스타일 |

## OpenAPI 클라이언트 생성

Server의 `/swagger/v1/swagger.json`을 사용해 클라이언트를 생성한다.

~~~bash
npm run gen:api
~~~

생성 파일 `src/api/generated/nexaone-api.ts`는 재생성 가능한 산출물이므로 Git에 포함하지 않는다.

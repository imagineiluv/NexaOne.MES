# NexaMes — Pro-Code SPA (React)

Frontend-Coexistence **Phase 2** 산출물. 기존 Blazor Server UI와 **공존**하며 동일한
**NexaOne.API**(REST + SignalR + JWT/permission)를 소비하는 React + Vite + TypeScript SPA다.
별도 프런트엔드 스택(React/Vue 등)이 백엔드 변경 없이 표준 계약 위에서 개발할 수 있음을 보여준다.

## 전제

- 실행 중인 **NexaOne.API** (JWT `Jwt:SecretKey`가 환경변수/UserSecrets로 설정되어 부팅되어야 함 — §18.7 fail-fast).
- Node.js 18+ / npm.

## 실행 (개발)

```bash
cd src/01.Web/NexaOne.Spa
npm install
copy .env.example .env        # (bash: cp) 그리고 VITE_API_PROXY를 실제 API 주소로 조정
npm run dev                   # http://localhost:5173
```

개발 모드는 `vite.config.ts`의 프록시로 `/api`·`/hubs`를 API로 전달하므로 **CORS 설정 없이** 동작한다.

## 운영 빌드

```bash
# .env: VITE_API_BASE_URL=https://api.nexames.example
npm run build                 # dist/ 정적 자산
```

운영에서는 SPA 오리진을 **API의 CORS `AllowedOrigins`**(appsettings)에 등록해야 한다. CORS 정책은
이미 다중 오리진 배열을 지원한다(`Program.cs`의 "NexaOne" 정책).

## API 타입/클라이언트 생성 (NSwag)

API가 OpenAPI 스펙을 **상시 노출**(`/swagger/v1/swagger.json`)하므로 TypeScript 클라이언트를 자동 생성한다:

```bash
# nswag.json의 url을 실행 중인 API 주소로 맞춘 뒤
npm run gen:api               # → src/api/generated/nexaone-api.ts (gitignore)
```

생성 전에는 `src/api/`의 손작성 타입(auth/client)으로 동작하며, 생성 후 점진적으로 교체한다.

## 구조

| 경로 | 역할 |
|------|------|
| `src/api/client.ts` | JWT Bearer fetch 래퍼 + 토큰 보관 |
| `src/api/auth.ts` | 로그인(`POST /api/v1/auth/login`) + 응답 계약 |
| `src/realtime/hub.ts` | SignalR `/hubs/smartees`(access_token 쿼리 인증) |
| `src/features/Login.tsx` | 로그인 화면 |
| `src/features/Dashboard.tsx` | 인증 REST 호출(FDC 파라미터 그룹) + 실시간 이벤트 수신 |
| `nswag.json` | OpenAPI → TS 클라이언트 생성 설정 |

## 다음 단계 (Phase 2 본격/Phase 3)

- 생성된 NSwag 클라이언트로 손작성 타입 대체, 도메인별 화면 확장.
- 토큰 갱신(`/api/v1/auth/refresh`) + permission 클레임 기반 UI 가드.
- Blazor 셸 내 임베드(마이크로프런트엔드: 커스텀 엘리먼트/iframe) — 단일 셸 공존(로드맵 Phase 3).

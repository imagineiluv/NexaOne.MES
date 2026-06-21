# 모든 모듈 API 확장 마스터플랜 (모듈별 소유 — 게이트웨이/브리지)

> 상태: 채택(분석 워크플로 12에이전트 종합, 2026-06-21). 모듈별 슬라이스를 순차 실행하며 본 문서를 갱신한다.

## 결정 1 — 모듈 명명: 전부 유지(개명 없음)

9개 모듈명(MDM/EST/FDC/RMS/QMS/CMMS/POM/SHP/SYS)을 모두 유지한다. 8/9가 MES 국제표준 약어이고(SHP만 비표준이나 명료), 레거시 SmartEES는 이미 더 표준형으로 진화했다(Micube.SmartEES: Ept→EST, Ems→CMMS, Ppm→POM, Dlv→SHP, SystemManagement→SYS). 개명은 네임스페이스(모듈당 60~100파일)·Spring xml·csproj·sln·게이트웨이 모듈명·git 이력을 churn하며 이득이 미미해 ROI가 음수다. 향후 신규 컴포넌트는 3자 대문자 표준약어(WMS/SCP/DMS 등)를 쓴다. (레거시 매핑·근거는 추후 ADR-009로 정식화 권장.)

## 결정 2 — 신규 모듈: 신설 없음(9개 유지)

10번째 plugin 모듈을 만들지 않는다. 도메인에 안 맞는 횡단 기능은 호스트 Default-ALC·기존 모듈·아키텍처 레이어로 귀속한다.

| 횡단 기능 | 귀속 | 근거 |
|---|---|---|
| 워크플로 오케스트레이션 | 보류(ADR-006 Phase4); 확정 시 RMS 확장 | 사용자-구성형이면 별도, 아니면 RMS 레시피 실행흐름 |
| 알림/경보 | SYS(정책·라우팅 소유) + Outbox/MessageBus(전달, ADR-002) | 정책=설정(SYS 도메인), 전달=이벤트 인프라(이미 동작) |
| 대시보드/분석 | 호스트 Default-ALC `AnalyticsQueryGateway` + `db/queries/analytics/` | 다중모듈 집계는 도메인 로직 아님(게이트웨이류 일반 서비스) |
| 배포/파일 | 호스트 최소 엔드포인트 또는 보류 | 인프라 운영, 저우선 |
| 데이터 보존 배치 | 이미 정합(모듈 워커 + Quartz, ADR-007) | 보존 기준=모듈 도메인, 스케줄러=인프라 |
| 다중-애그리거트 사가(POM Mixing, SYS Approve) | UnitOfWork 레이어 선결(아키텍처 백로그) | 모듈이 아니라 트랜잭션 조정 역량 필요 |

## 결정 3 — 분류 원칙(dual-route 정책)

- **브리지(ADR-008)**: 단일 애그리거트 상태전이/도메인 불변식/낙관적 동시성/원자 쓰기 → `NexaOne.ServiceContracts`의 `IXxxBridge` + 모듈 어댑터 빈 + 호스트 얇은 컨트롤러(Result→409/400/200). EST/RMS/SHP 패턴.
- **게이트웨이(ADR-001)**: 순수 CRUD·조회·콤보 → `db/queries/{mssql,sqlite}/{MOD}.xml` 명명쿼리, `/api/v1/query|command/{id}`. QMS 패턴.
- **생성 연산의 dual-route 정책**: 도메인 불변식이 있는 생성(예: 팩토리 검증)은 **브리지**, 단순 INSERT는 **게이트웨이**. 모듈별로 일관 적용.
- **워커(ADR-006)**: 실시간/하드웨어/주기 배경(FDC 수집, 보존) → 모듈 소유 BackgroundService, REST 아님.
- **보류**: 다중 애그리거트·다단계 트랜잭션(POM Lot Mixing, SYS 승인 일부) → UnitOfWork 선결.

## 실행 순서 (순차 — 공유파일[Program.cs/sln/ServerTests] 충돌 방지, 게이트웨이 먼저·브리지 1개씩)

| # | 모듈 | 슬라이스 | kind | 규모 |
|---|---|---|---|---|
| S1 | SHP | 잔여 게이트웨이: 상태별 카운트·주문별 품목/이력 조회(read), 품목 실적/이력 기록(write). 브리지 완료. | gateway | S |
| (옵션) | RMS | RMS 상태 카운트 게이트웨이(선택). 승인 브리지 완료. | gateway | XS |
| S2 | QMS | 게이트웨이 쓰기 5 + 브리지 2(ConfirmDefect, UpdateControlLimits). | both | M |
| S3 | EST | 브리지 `IEquipmentAlarmBridge`(RecordAlarm/ClearAlarm/GetActiveAlarms/Count). EstBridgeController 확장. | bridge | M |
| S4 | MDM | 브리지 `IMdmEquipmentBridge`+`IMdmMasterBridge`(생성/비활성/갱신 — 불변식), 게이트웨이 조회/콤보(대부분). 첫 미노출-모듈 부팅. | both | L |
| S5 | CMMS | 브리지 WO/Plan/Part 상태전이 + 게이트웨이 조회. Plan→WO 캐스케이드 보류. | both | L |
| S6 | POM | 브리지 Plan/Order/Lot(TrackIn/Out/Hold) + 게이트웨이 조회. **Lot Mixing(다중애그리거트) 보류**. | both | XL |
| S7 | SYS | 브리지 잔여 Auth/User+Registration + 게이트웨이 CRUD. **Approve(다중애그리거트) 보류**. login/refresh는 호스트 기존 — 마지막. | both | XL |
| S8 | FDC | 게이트웨이 설정 조회 + `IFdcBridge` 3 ops. **실시간 수집/평가/워커는 워커 유지(REST 아님)**. | both | L |

## 검증 게이트(슬라이스마다)
- 전체 sln 빌드 0 errors + ServerTests 녹색. 브리지 슬라이스는 **HostModulesBootSmokeTests**가 실 modules-ON 부팅에서 새 브리지 GetBean→캐스트(plugin ALC 횡단)를 자동 검증한다.
- 컨트롤러 단위 E2E(modules-OFF + Fake 브리지)로 HTTP 매핑·권한·Result 분기. 게이트웨이는 SQLite 라운드트립(가능 시).
- 권한: 쓰기는 모듈별 `*:manage`(Permissions). 읽기는 인증만.
- 읽기경로 Restore 무손실 회귀 주의(MEMORY): Row.ToDomain은 Restore. 생성→전이→GET로 커버.

## 리스크
공유파일로 순차 강제(게이트웨이 먼저, 브리지 1개씩). ALC 동일성(브리지 캐스트 null=ServiceContracts plugin-ALC 중복로드=부팅 hard fail → 모듈 게시 deps-제외 확인, 부팅 fail-fast로 검출). 미노출 모듈(MDM/CMMS/POM/SYS/FDC)은 브리지 빈 추가 + Program.cs 등록 + 실부팅 검증 — MDM이 그 리스크를 먼저 흡수. 생성 dual-route 정책 일관. 보류 항목(POM Mixing, SYS Approve)은 UnitOfWork 선결로 명시 제외.

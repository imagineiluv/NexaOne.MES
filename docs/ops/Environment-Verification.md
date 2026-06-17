# 환경 의존 검증 가이드 (NexaOne.Server 런북)

이 문서는 **이 저장소 CI/로컬에서 자동 검증이 불가한** 경로(실 MSSQL, Kafka 다중 프로세스, OPC-UA 설비, 스케줄 워커 가동)를 사용자 환경에서 검증하는 절차다. 자동 스위트(단위 1067 / 통합 263, SQLite)가 커버하는 범위 밖만 다룬다.

설계 근거: [ADR-004](../design/adr/ADR-004-server-host-runtime.md)(호스트·DB 전환), [ADR-005](../design/adr/ADR-005-server-service-container.md)(서비스 빈 컨테이너), [ADR-006](../design/adr/ADR-006-web-worker-separation.md)(웹/워커 분리), [ADR-007](../design/adr/ADR-007-recurring-scheduler.md)(주기 스케줄러).

## 환경 매트릭스

| 경로 | 자동 검증(여기) | 사용자 환경 필요 |
|------|------------------|------------------|
| SQLite 로컬 부팅 | ✅ 가능 | — |
| MSSQL 실부팅 | ❌ | SQL Server |
| Kafka cross-process(워커→SignalR) | ❌ | Kafka 브로커 |
| OPC-UA 수집 | ❌(Skip) | OPC-UA 서버/시뮬레이터 |
| 스케줄 워커 가동 | ✅ 발화/SQL은 SQLite로 확인 | 실 효과는 위 인프라 동반 |

전환은 모두 **코드 재빌드 없이 server.xml / appsettings 설정만** 바꾼다.

---

## A. SQLite 로컬 부팅 (baseline — 외부 인프라 불필요)

`src/00.Main/NexaOne.Server/server.xml`의 `[MSSQL]` 3개 객체(dbProvider/eesDialect/eesDataSource)를 주석 처리하고 `[SQLite]` 블록을 활성화한 뒤:

```
dotnet run --project src/00.Main/NexaOne.Server
```

기대 로그: `SQLite mode — ensuring schema` → `Schema ready` → 9개 모듈 `Service '..' registered` → `N background worker(s) discovered` → `Ready`. db/migrations를 SQLite 방언으로 자동 변환해 `nexaone.db`를 생성한다(빈 DB일 때만).

---

## B. MSSQL 실부팅

1. **스키마 준비**: SQLite와 달리 운영은 자동 부트스트랩하지 않는다. `db/migrations/V*.sql`을 대상 DB에 순서대로 적용한다(sqlcmd/Flyway 등).
2. **server.xml**: `[MSSQL]` 블록 활성화(기본값), `eesDataSource`의 ConnectionString을 실 서버로:
   ```xml
   <object id="eesDataSource" ...>
     <property name="Provider" ref="dbProvider" />
     <property name="ConnectionString" value="Server=...;Database=NexaOneEES;..." />
   </object>
   ```
3. **기동·확인**: `dotnet run --project src/00.Main/NexaOne.Server` → `Server context initialized` → 9개 모듈 `registered` → `Ready`, stderr 없음.
   - 검증 포인트: 자식 컨텍스트 9개가 부모 공통 빈(eesDataSource 등)을 정상 주입(타입 동일성). 리포는 ctor에서 DB에 연결하지 않으므로 `Ready` 도달 = 와이어링 정상. 실제 쿼리는 아래 워커/API로 확인.
4. **API 티어**: `src/02.Backend/NexaOne.API/appsettings.json`의 `Database:Provider=MsSql` + `ConnectionStrings:NexaOne` 동일 설정 후 기동.

---

## C. Kafka cross-process (워커 → API SignalR)

워커를 별도 프로세스(Server)로 분리하면 인메모리 버스로는 웹 SignalR에 닿지 않는다 — Kafka 백본 필요(ADR-006).

1. **브로커 가동**(예 localhost:9092).
2. **server.xml messageBus를 Kafka로 전환**:
   ```xml
   <object id="messageBus" type="NexaOne.Infrastructure.Messaging.KafkaMessageBus, NexaOne.Infrastructure.Messaging">
     <constructor-arg value="localhost:9092" />
   </object>
   ```
   (기본 `InMemoryMessageBus` 줄은 주석 처리.)
3. **API 구독자 활성**: appsettings `Kafka:Enabled=true` + `Kafka:BootstrapServers` 설정 → API가 Kafka→SignalR 구독자를 띄운다(Program.cs).
4. **워커 가동**: Server측 워커(예 scheduledOutboxDispatchWorker, fdcCollectionWorker)의 모듈/server xml `enabled="true"`.
5. **검증**: 워커가 도메인 이벤트를 발행 → API 구독자가 수신 → 브라우저 SignalR로 실시간 갱신. Kafka 토픽(`nexaone.events`)에서 메시지 흐름 확인.

---

## D. OPC-UA 설비 수집 (FDC)

1. **OPC-UA 서버/시뮬레이터** 준비(NexusLogic.Plc.Simulator 또는 실 설비).
2. **엔드포인트 시드**: `FDC_EQUIPMENT_ENDPOINT`에 활성 OPC-UA 엔드포인트 행 등록(DriverKind=OpcUa).
3. **수집 가동(택1)**:
   - API 호스티드: appsettings `Fdc:Collector:Enabled=true`(FdcCollectorHostedService).
   - 또는 Server 모듈 워커: `fdc.xml`의 `fdcCollectionWorker` `enabled="true"`(ADR-006 Phase 2). OPC-UA 드라이버는 server.xml `opcUaDriver` 공통 빈을 사용.
4. **검증**: 디바이스 연결 → 태그 구독 → `FDC_COLLECT_DATA` 적재 → 인터락/알람 평가 → 이벤트(인터락/알람)→(Kafka 시) SignalR.
5. **통합 테스트 Skip 해제**: `test/NexaOne.IntegrationTests/Fdc/FdcCollectorIntegrationTests.cs`의 `[Fact(Skip=...)]`를 OPC-UA 시뮬레이터 + (실 MSSQL 불요, SQLite 가능) 환경에서 해제해 실연결 검증.

---

## E. 스케줄 워커 가동 (ADR-007)

각 모듈 xml의 워커 `enabled` 생성자 인자를 `true`로 바꾼다(기본 OFF). 발화·SQL 실행 자체는 SQLite에서도 확인된다(아래는 운영 의미).

| 워커 | 위치 | enable 인자 | 기본 간격 |
|------|------|-------------|-----------|
| scheduledOutboxDispatchWorker | server.xml | enabled | 5s (PollInterval) |
| fdcCollectionWorker | fdc.xml | enabled | (구독, 비주기) |
| fdcCollectDataRetentionWorker | fdc.xml | enabled | 1일·보존 30일 |
| maintenanceDueCheckWorker | cmms.xml | enabled | 1시간 |
| loginFailureRetentionWorker | sys.xml | enabled | 1일·보존 90일 |

검증(로컬 SQLite로도 가능): 워커 `enabled="true"` + 간격을 짧게(예 2s) → 기동 로그에 워커 `started` + 주기 실행 로그(예 `purged N row(s)`) 확인, stderr 없음. Outbox/이벤트 발행이 웹에 닿는지는 위 C(Kafka) 동반.

---

## 체크리스트(요약)

- [ ] MSSQL: db/migrations 적용 → server.xml [MSSQL] + conn string → `Ready` 부팅, 9개 모듈 registered.
- [ ] Kafka: 브로커 → server.xml messageBus=Kafka → API Kafka:Enabled=true → 워커 enable → 이벤트→SignalR 흐름.
- [ ] OPC-UA: 시뮬레이터 → FDC_EQUIPMENT_ENDPOINT 시드 → 수집 enable → FDC_COLLECT_DATA 적재 → Skip 테스트 해제.
- [ ] 스케줄 워커: 모듈 xml enabled=true → 발화·SQL 로그 확인.

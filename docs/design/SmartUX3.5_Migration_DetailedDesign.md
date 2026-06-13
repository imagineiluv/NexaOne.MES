# SmartUX 3.5 → C# 마이그레이션 상세설계 문서

**문서 버전:** 2.0  
**기준 소스:** SmartUX3.5_20260526  
**작성일:** 2026-06-08  
**보완일:** 2026-06-12 (v2.0)  
**작성 목적:** 현행 SmartUX 3.5 시스템을 순수 C# 기반으로 기능·UI 동일하게 이전하기 위한 상세 설계

> **v1.2 변경 이력:** Program.cs 부트스트랩(LoginForm + 3개 InjectModule), UserInfo.Current 싱글톤, ConditionCollection 13종 컨트롤, Form 계층 10종, SO 감사 메커니즘, EMS/PPM/DLV 도메인, JWT Refresh Token/CORS, 워크플로우 WorkflowContext, FDC/RMS/QMS DB 테이블, SignalR 5-Hub 구조, 솔루션 구조 완성, 목차 섹션 15~20 추가  
> **v1.3 변경 이력:** lo-LO 라오어 5번째 언어 추가(4.1.3), SqlTxnContext 멀티DB 채번 분기(5.3.4), ServiceObjectProcessor.InsertAsync 완전 구현 예시(5.3.3), WorkflowRequest DTO 클래스 정의(8.2), 멀티DB 쿼리 런타임 선택 로직(7.3), Framework/모듈 csproj 추가(3.3), WinForms CORS 미적용 범위 명기(9.6)  
> **v1.4 변경 이력:** 솔루션 구조 재설계 — DB 드라이버 레이어(`03.Driver`)와 도메인 모듈 레이어(`04.Modules`) 명확히 분리, `IDbDriver` 인터페이스 정의(3.3), 드라이버별 csproj + 구현체 설계(3.4), `SqlTxnContext`를 `IDbDriver` 기반으로 단순화(3.5/5.3.4)  
> **v1.5 변경 이력:** `IDbDriver` 기능 6그룹 확장 — ①연결관리 ②채번/시각 ③쿼리빌더(Upsert/BatchInsert/TempTable/Concat 등) ④벌크Insert ⑤스키마조회 ⑥진단, MSSQL/PostgreSQL/MySQL 전체 구현 코드(3.3~3.5)  
> **v1.6 변경 이력:** DB 외 드라이버 설계 추가(3.6) — Messaging/Equipment/Notification/Auth/Cache/FileStorage 인터페이스 및 구현체 초안  
> **v1.7 변경 이력:** 드라이버 3개 카테고리로 재편(3.1/3.6) — **DB**(01.Db) / **통신**(02.Communication: Kafka·RabbitMq·OpcUa·Serial·Mqtt·SmtpEmail·Sms·Ldap) / **캐시**(03.Cache: Redis·MemoryCache). FileStorage → SmartEES.Infrastructure로 이동, DbAuth → Infrastructure 서비스로 분리, LDAP → IExternalAuthDriver(통신 드라이버)로 재정의  
> **v1.8 변경 이력:** **NexusFramework / NexusCom 서브모듈 아키텍처 기반 리팩토링** — ①서브모듈 솔루션 구조 반영(3.1), ②NexusCom `IDatabaseProvider` 패턴으로 DB 드라이버 재정의(3.3/3.6), MSSQL 공급자 신규 구현 명시(`NexusCom.Data.MsSql`), ③NexusFramework `FlowExecutionEngine`/`WorkflowManager`/`WorkflowExecutor` 기반 워크플로우 엔진 재설계(8), `AssemblyInvocationNode` + `[WorkflowCallable]` 패턴 도입, ④RuleExecutor에 `[WorkflowCallable]` 연계 추가(5.3.1), ⑤FDC `PlantController`/`Machine`/`IDeviceInterface` 패턴 적용(10.4)  
> **v1.9 변경 이력:** **UI 레이어 C# Blazor Server 기반으로 전면 재설계** — ①WinForms/DevExpress WinForms 제거, ASP.NET Core Blazor Server 아키텍처 수립(6), ②마이그레이션 목표·범위 업데이트(2.1/2.2), ③Phase 2 Blazor 웹 UI 구축으로 변경(2.3), ④SmartConditionGrid / SmartGrid 공통 컴포넌트 설계(6.6), ⑤WinForms 폼 클래스 → Blazor 컴포넌트 매핑표 추가(6.9), ⑥NexaOne.Web 솔루션 구조 추가(6.2)  
> **v2.0 변경 이력:** **드라이버 소속 재정의 — 프로토콜 구현은 서브모듈(NexusCom/NexusLogic) 소유** — ①`NexusCom.Data.MsSql`을 `03.Driver/01.Db/`에서 NexusCom 서브모듈(`submodules/NexusCom/src/`)로 이관 완료, SQL 방언 인터페이스를 `INexaOneEESDbCapability`로 개명하여 `NexusCom.Data.Abstractions`에 흡수(3.1/3.3~3.5), ②통신 드라이버를 NexusCom bounded context로 재배치(3.1/3.6) — 메시징(Kafka·RabbitMq·Mqtt)→`NexusCom.Messaging.*`, 알림(SmtpEmail·Sms)→`NexusCom.Notify.*`, 외부 인증(Ldap)→`NexusCom.Directory.*`, ③설비 프로토콜(OpcUa·SerialPort)은 NexusLogic(구 `NexusCom.Plc.*` 이관처) 소속으로 정의 — NexaMes `03.Driver/02.Communication/`에는 `IDeviceInterface` 어댑터 등 SmartEES 전용 글루와 이관 전 과도기 구현만 잔류, ④문서-코드 정합화 — MsSqlProvider 코드 샘플을 실제 서브모듈 구현과 일치(3.6 카테고리1), `SqlTxnContext` 중복 설계를 3.5로 일원화(5.3.4), csproj 예시를 서브모듈 경로·3단계 상대 참조로 수정, `SmartEES.Driver.PostgreSQL` 래퍼 예시를 `NexusCom.Data.PostgreSql` 직접 사용으로 교체, ⑤`NexusCom.Messaging.Kafka` 이관 완료(3.6.1) — NexaOne.Driver.Kafka 삭제, 순수 드라이버(Confluent.Kafka + ILogger)로 서브모듈 소속, 앱 측 글루(`NexaOne.Infrastructure.Messaging`의 KafkaMessageBus·KafkaConsumerService)를 KafkaDriver 래핑으로 리팩토링하여 앱의 Confluent.Kafka 직접 참조 제거(전이 참조로 전환), `IMessageBrokerDriver` 계약 어댑터는 브로커 교체 요구 시 도입(보류), ⑥설비 프로토콜 확정 — **NexusLogic 서브모듈 추가**(`submodules/NexusLogic`, NexusCom과 sibling), OPC-UA는 `NexusLogic.Plc.OpcUa`(IPlcDriver)를 그대로 활용하기로 확정하고 NexaOne.Driver.OpcUa 자체 구현 삭제, **SerialPort는 NexusLogic 미제공이므로 NexaOne.Driver.SerialPort 삭제 및 범위 제외**(3.1/3.2/3.6/10.4), ⑦드라이버 재배치 마무리 — **NexaOne.Driver.SmtpEmail → NexusCom.Notify.SmtpEmail 이관 완료**(앱 글루 SmtpEmailSender는 IEmailSender로 래핑), **OpcUaDeviceInterface 어댑터 구현**(NexaOne.FDC, IDeviceInterface ↔ IPlcDriver, 단위 테스트 8개), **INexaOneEESDbCapability 12멤버 전부 구현**(잔여 7멤버 추가, MsSqlProvider) — 02.Communication/에 자체 프로토콜 구현이 0개로 정리됨(3.3/3.6.1/3.6.3/3.6.4)

---

## 목차

1. [현행 시스템 분석](#1-현행-시스템-분석)
2. [마이그레이션 목표 및 전략](#2-마이그레이션-목표-및-전략)
3. [타겟 C# 솔루션 구조 설계](#3-타겟-c-솔루션-구조-설계)
4. [프레임워크 레이어 상세 설계](#4-프레임워크-레이어-상세-설계)
5. [백엔드 서비스 레이어 설계 (Java → C# 전환)](#5-백엔드-서비스-레이어-설계-java--c-전환)
6. [UI 레이어 상세 설계 (C# Blazor 웹)](#6-ui-레이어-상세-설계-c-blazor-웹)
7. [데이터베이스 설계](#7-데이터베이스-설계)
8. [워크플로우 엔진 설계](#8-워크플로우-엔진-설계)
9. [보안 / 인증 / 권한 설계](#9-보안--인증--권한-설계)
10. [도메인 모듈별 상세 설계](#10-도메인-모듈별-상세-설계)
11. [설정 및 인프라 설계](#11-설정-및-인프라-설계)
12. [마이그레이션 단계별 계획](#12-마이그레이션-단계별-계획)
13. [기술 스택 매핑표](#13-기술-스택-매핑표)
14. [소스 검증 기반 보완 상세 설계](#14-소스-검증-기반-보완-상세-설계)
15. [캐싱 전략 설계](#15-캐싱-전략-설계)
16. [CI/CD 파이프라인 설계](#16-cicd-파이프라인-설계)
17. [헬스체크 / 모니터링 / Metrics 설계](#17-헬스체크--모니터링--metrics-설계)
18. [아키텍처 보완 설계](#18-아키텍처-보완-설계)
19. [기능 보완 설계 (누락 항목)](#19-기능-보완-설계-누락-항목)
20. [기능 보완 설계 (부족 항목)](#20-기능-보완-설계-부족-항목)

---

## 1. 현행 시스템 분석

### 1.1 시스템 개요

SmartUX 3.5는 **제조실행시스템(MES, Manufacturing Execution System)** 으로, 스마트 팩토리 환경에서 설비 관리, 품질 관리, 생산 추적, 마스터데이터 관리 등을 지원하는 엔터프라이즈 시스템입니다.

| 항목 | 내용 |
|------|------|
| 시스템명 | SmartUX 3.5 (SmartEES) |
| 기준버전 | SmartUX3.5_20260526 |
| 주요 도메인 | 설비관리(EPT), 마스터데이터(MDM), 품질관리(QMS), 설비데이터수집(FDC), 레시피관리(RMS) |
| 사용자 유형 | 현장 작업자, 관리자, 시스템 관리자 |
| 다국어 지원 | ko-KR, en-US, zh-CN, vi-VN, lo-LO |

### 1.2 현행 아키텍처

```
┌─────────────────────────────────────────────┐
│           프레젠테이션 레이어                  │
│  ┌──────────────────┐  ┌──────────────────┐  │
│  │   Desktop Client  │  │    Web Client     │  │
│  │  C#/.NET WinForms │  │  HTML5/JavaScript │  │
│  │  DevExpress v18.1 │  │  Custom Widget FW │  │
│  └────────┬─────────┘  └────────┬──────────┘  │
└───────────┼───────────────────── ┼─────────────┘
            │ WCF/HTTP             │ HTTP/REST
┌───────────┼──────────────────────┼─────────────┐
│           │   비즈니스 로직 레이어  │              │
│  ┌────────▼──────────────────────▼──────────┐  │
│  │         Java OSGi 백엔드 (Eclipse 플러그인) │  │
│  │  - 50+ 컴포넌트 번들 (s-component-*)       │  │
│  │  - 40+ 규칙 모듈 (s-rule-*)               │  │
│  │  - 3 통신 모듈 (s-communication-*)        │  │
│  │  - Kafka, WebSocket, REST API             │  │
│  └─────────────────┬─────────────────────────┘  │
└────────────────────┼────────────────────────────┘
                     │ JDBC
┌────────────────────┼────────────────────────────┐
│           데이터 레이어                            │
│  MSSQL / PostgreSQL / MySQL / MariaDB / Oracle  │
└─────────────────────────────────────────────────┘
```

### 1.3 현행 기술 스택

#### 데스크탑 클라이언트 (C#)

| 구성 요소 | 기술 | 버전 |
|-----------|------|------|
| 런타임 | .NET Framework | 4.5 / 4.0 |
| UI 프레임워크 | Windows Forms | - |
| UI 컨트롤 | DevExpress | 18.1 |
| DI 컨테이너 | Ninject | 3.2~3.3.4 |
| JSON 직렬화 | Newtonsoft.Json | - |
| YAML 파서 | YamlDotNet | - |
| 매핑 | AutoMapper | 6.2.2 |
| 서버 통신 | WCF / HTTP | - |

#### 웹 클라이언트 (Web)

| 구성 요소 | 기술 |
|-----------|------|
| 기반 | HTML5 / CSS3 / JavaScript |
| 서블릿 컨테이너 | Jetty (Jakarta EE 5.0) |
| 위젯 프레임워크 | Micube 커스텀 컴포넌트 |
| 데이터 그리드 | 커스텀 DataGrid/TreeGrid |

#### 백엔드 (Java)

| 구성 요소 | 기술 |
|-----------|------|
| 런타임 | Java 17 |
| 아키텍처 | OSGi (Eclipse Plugin) |
| 빌드 | Maven/Eclipse |
| 메시징 | Kafka, WebSocket, Socket |
| API | REST API, GraphQL |
| ORM | 커스텀 Service Object (SO) 패턴 |

#### 데이터베이스

| DB | 용도 |
|----|------|
| MSSQL | 주 운영 DB |
| PostgreSQL | 대체 DB |
| MySQL/MariaDB | 대체 DB |
| Oracle | 레거시 지원 |
| SQLite | 임베디드 |

### 1.4 현행 프로젝트 파일 구성

```
SmartUX3.5_20260526/
├── DOTNET_UI_DEMO/
│   └── client_source/
│       ├── Framework.sln                     ← 메인 솔루션
│       └── src/
│           ├── Micube.Framework/             (35+ .cs)
│           ├── Micube.Framework.Net/         (37+ .cs)
│           ├── Micube.Framework.SmartControls/ (200+ .cs)
│           ├── Micube.Framework.Net.Wcf/
│           ├── Micube.Framework.Log/
│           ├── SmartEES/                     (40+ .cs)
│           ├── Micube.SmartEES.Ept/          (60+ .cs)
│           ├── Micube.SmartEES.Fdc/
│           ├── Micube.SmartEES.Mdm/          (50+ .cs)
│           ├── Micube.SmartEES.Rms/
│           └── Micube.SmartEES.SystemManagement/
├── www/                                      ← 웹 UI
│   ├── webapps/ROOT/WEB-INF/web.xml
│   ├── content/MICUBE_STANDARD/             (1114 HTML)
│   └── package/                             (위젯/컴포넌트)
├── Config/
│   ├── SO/                                  ← 서비스 오브젝트 정의
│   ├── Datasource/                          ← DB 접속 설정
│   ├── Schema/                              ← DB DDL (37개 SQL)
│   ├── Query/xml/                           ← SQL 쿼리 정의
│   ├── Workflow/                            ← 워크플로우 정의
│   └── Message/                             ← 이벤트 메시지 설정
├── s-component-*/                           ← Java 컴포넌트 번들 (15개)
├── s-rule-*/                                ← Java 비즈니스 규칙 (40개)
└── s-communication-*/                       ← Java 통신 모듈 (3개)
```

### 1.5 주요 소스 코드 분석

#### 1.5.1 핵심 클래스 목록

| 클래스 | 위치 | 역할 |
|--------|------|------|
| `MainForm` | SmartEES/ | 메인 애플리케이션 윈도우, 메뉴 관리 |
| `LoginForm` | SmartEES/ | 로그인 UI, 다국어/플랜트 선택 |
| `FormCreator` | SmartEES/ | 동적 폼 생성 팩토리 (DLL 로드) |
| `FrameworkSettings` | SmartEES/ | 앱 초기화, 언어/리소스 로딩 |
| `AppConfiguration` | Micube.Framework/ | YAML 기반 설정 관리 |
| `EventAggregator` | Micube.Framework/ | 퍼블리시/서브스크라이브 이벤트 버스 |
| `Language` | Micube.Framework/ | 다국어 관리 (Dictionary/Message) |
| `MessageWorker` | Micube.Framework.Net/ | 네트워크 메시지 전송 파사드 |
| `SqlExecuter` | Micube.Framework.Net/ | 쿼리/프로시저 실행 헬퍼 |
| `ChannelProxy` | Micube.Framework.Net/ | 트랜스포트 레이어 팩토리 |
| `SmartBaseForm` | SmartControls/Forms/ | 모든 폼의 기본 클래스 |
| `SmartConditionBaseForm` | SmartControls/Forms/ | 검색/결과 폼 기본 클래스 |
| `Equipment` | Micube.SmartEES.Mdm/ | 설비 마스터 관리 폼 |
| `EquipmentAlarmHistory` | Micube.SmartEES.Ept/ | 설비 알람 이력 분석 폼 |

#### 1.5.2 핵심 디자인 패턴

| 패턴 | 적용 위치 | 설명 |
|------|-----------|------|
| Dependency Injection | Ninject, NinjectProgram | IoC 컨테이너 기반 DI |
| Repository Pattern | MenuRepository, SettingRepository | 데이터 접근 추상화 |
| Pub/Sub (Event Aggregator) | EventAggregator | 폼 간 이벤트 전달 |
| Factory Pattern | FormCreator, ChannelProxy | 동적 인스턴스 생성 |
| Template Method | SmartBaseForm, SmartConditionBaseForm | 폼 라이프사이클 관리 |
| Fluent Builder API | Grid/Condition 컬럼 정의 | 선언적 UI 구성 |
| Strategy Pattern | IMessageChannel, IMessageSerializer | 네트워크 전송 레이어 교체 |
| Weak Reference Observer | EventAggregator | 메모리 누수 방지 |
| Configuration Provider | AppConfiguration (YAML) | 설정 타입 안전 접근 |
| Facade | MessageWorker, SqlExecuter | 복잡한 네트워크 로직 추상화 |

---

## 2. 마이그레이션 목표 및 전략

### 2.1 마이그레이션 목표

1. **기능 동일성**: 현행 모든 업무 기능 100% 보존
2. **UI 현대화**: WinForms → C# Blazor Server 웹 UI, 브라우저에서 화면 접근
3. **기술 단일화**: Java 백엔드 → C# .NET으로 완전 통합
4. **현대화**: .NET 8 LTS로 업그레이드, C# Blazor Server 웹 UI로 전환
5. **성능 향상**: ASP.NET Core 기반 고성능 백엔드 서비스

### 2.2 마이그레이션 범위

| 레이어 | 현행 | 타겟 | 변경 유형 |
|--------|------|------|-----------|
| UI | C# WinForms / .NET 4.5 | **C# Blazor Server / .NET 8** | **전환** |
| 웹 UI | HTML5/JS (Jetty) | ASP.NET Core Blazor Server | 전환 |
| 비즈니스 로직 | Java OSGi (50+ 컴포넌트) | C# ASP.NET Core 서비스 | 재작성 |
| API 레이어 | Java REST/WCF | C# ASP.NET Core Web API | 재작성 |
| 메시지 처리 | Java Kafka/WS | C# SignalR / Kafka.NET | 전환 |
| DB 접근 | Java SO 패턴 (JDBC) | Dapper + ServiceObjectProcessor | 전환 |
| 워크플로우 | Java 커스텀 엔진 | NexusFramework WorkflowManager | 전환 |
| 인증/권한 | Java 커스텀 | ASP.NET Core Identity / JWT | 전환 |

### 2.3 마이그레이션 전략

**단계적 Strangler Fig 패턴 적용** (상세 일정은 섹션 12 참조)

```
Phase 1: 기반 인프라 구축 (2~3주)
  └─ C# 백엔드 솔루션 골격 생성 (NexusFramework/NexusCom 서브모듈 연동)
  └─ EesDataSource + QueryRepository + ServiceObjectProcessor 구현
  └─ JWT 인증 API (NexaOne.API), DB 연결 구축
  └─ NexaOne.Server Spring.NET XML DI 구성 (server.xml / nexaone-ees.xml)

Phase 2: C# Blazor 웹 UI 구축 (4~5주)
  └─ NexaOne.Web 프로젝트 생성 (Blazor Server)
  └─ SmartConditionGrid / SmartGrid / SmartPopup 공통 컴포넌트 구현
  └─ MainLayout / NavMenu / Login 화면 구현
  └─ 9개 도메인 업무 페이지 (.razor) 구현 (MDM/EPT/FDC/RMS/QMS/EMS/PPM/DLV/Sys)

Phase 3: 백엔드 비즈니스 로직 이전 (8~10주)
  └─ MDM/SystemManagement → EPT → FDC → RMS → QMS → EMS → PPM → DLV 순차 이전
  └─ Java Rule → C# RuleExecutor, Java SO → ServiceObjectProcessor+감사메커니즘
  └─ WorkflowEngine 구현 (NexusFramework), SignalR + Kafka.NET 메시지 파이프라인

Phase 4: 통합 검증 및 Java 제거 (2~3주)
  └─ 기능 동일성·성능 검증, UAT
  └─ 검증 완료 후 Java 시스템 종료
```

---

## 3. 타겟 C# 솔루션 구조 설계

### 3.1 전체 솔루션 구성

> **설계 원칙:**
> - **서브모듈 프레임워크** (`submodules/`) — NexusFramework(워크플로우 엔진 + 설비 제어)와 NexusCom(DB 공급자 + 통신 드라이버)을 git 서브모듈로 관리. 코어 구현을 재사용하되 SmartEES 전용 확장만 추가.
> - **프로토콜 구현은 서브모듈 소유 (v2.0)** — DB·메시징·알림·외부 인증 드라이버는 NexusCom이, 설비 프로토콜(OPC-UA 등)은 NexusLogic(구 `NexusCom.Plc.*` 이관처)이 소유한다. NexaMes `03.Driver/`에는 SmartEES 전용 글루(어댑터)와 이관 전 과도기 구현만 둔다.
> - **DB 드라이버** — NexusCom 공급자 프로젝트를 직접 참조. MSSQL은 `NexusCom.Data.MsSql`로 **서브모듈에 구현 완료**(`submodules/NexusCom/src/`, v2.0 이관).
> - **도메인 모듈** (`04.Modules`) — 비즈니스 기능(화면+서비스)을 도메인 단위로 분리. 각 모듈은 `./Modules/` 디렉토리에 DLL로 배포.
> - 두 레이어는 서로 의존하지 않으며, 공통 인터페이스(`SmartEES.Infrastructure`)를 통해서만 연결된다.

#### 서브모듈 구성

```
submodules/
├── NexusFramework/                           ← 워크플로우 엔진 + 설비 제어 프레임워크
│   └── src/NexusFramework/
│       ├── Workflow/Runtime/FlowExecutionEngine.cs    ← 그래프 실행 엔진 (핵심)
│       ├── Workflow/Tooling/WorkflowManager.cs        ← 다중 실행기 관리 + 동시성 제어
│       ├── Workflow/Tooling/WorkflowExecutor.cs       ← .workflow/.aigraph.json 파일 로드 + 실행
│       ├── Workflow/Nodes/AssemblyInvocationNode.cs   ← 외부 어셈블리 메서드 호출 노드
│       ├── Workflow/Reflection/WorkflowCallableAttribute.cs ← 비즈니스 메서드 마킹 어트리뷰트
│       ├── PlantController.cs                         ← 설비 제어 컨트롤러 (Machine 관리)
│       ├── Machine.cs                                 ← 개별 설비 단위
│       ├── Resource/IDeviceInterface.cs               ← 설비 드라이버 인터페이스
│       ├── Resource/IStateMachine.cs                  ← 상태 머신 인터페이스
│       └── Context/FileSystemApplicationContext.cs    ← Spring.NET IoC 컨텍스트
│
└── NexusCom/                                 ← DB 공급자 + 통신 드라이버 레이어
    └── src/
        ├── NexusCom.Data.Abstractions/
        │   └── Interfaces/Contracts.cs       ← IDatabaseProvider, IQueryExecutor,
        │                                        IMetadataProvider, ITransactionManager,
        │                                        IChangeFeedProvider, IDriverManager,
        │                                        INexaOneEESDbCapability (v2.0 흡수)
        ├── NexusCom.Data.MsSql/              ← ★ MSSQL 공급자 (v2.0 — 03.Driver에서 이관 완료)
        ├── NexusCom.Data.PostgreSql/         ← PostgreSQL 공급자 (완성)
        ├── NexusCom.Data.MySql/              ← MySQL/MariaDB 공급자 (완성)
        ├── NexusCom.Data.Sqlite/             ← SQLite 공급자 (완성)
        ├── NexusCom.Data.EFCore/             ← EF Core SaveChangesInterceptor 어댑터
        ├── NexusCom.Data.Core/               ← DriverManager, ChangeEventDispatcher
        ├── NexusCom.Data.Hosting/            ← DataHostedService, DataEndpointHealthCheck
        ├── NexusCom.Messaging.Kafka/         ← ★ Kafka 메시징 (v2.0 이관 완료 — Confluent.Kafka)
        ├── NexusCom.Messaging.*/             ← (계획) RabbitMq·Mqtt 메시징 컨텍스트
        ├── NexusCom.Notify.SmtpEmail/        ← ★ SMTP 이메일 (v2.0 이관 완료 — MailKit)
        ├── NexusCom.Notify.*/                ← (계획) Sms 알림 컨텍스트
        └── NexusCom.Directory.*/             ← (계획) Ldap 외부 인증 컨텍스트

NexusLogic/                                   ← ★ 설비 프로토콜 레이어 (v2.0 서브모듈 추가 완료, 구 NexusCom.Plc.* 이관처)
    └── src/
        ├── NexusLogic.Plc.Abstractions/      ← IPlcDriver, PlcEndpoint, IPlcConnection, PlcDriverCapabilities
        ├── NexusLogic.Plc.Core/              ← polling host, last value cache, rule evaluator
        ├── NexusLogic.Plc.OpcUa/             ← ★ OPC-UA 드라이버 (IPlcDriver) — NexaMes가 활용
        ├── NexusLogic.Plc.ModbusTcp/         ← Modbus TCP 드라이버
        ├── NexusLogic.Plc.MitsubishiMc/      ← Mitsubishi MC 3E 드라이버
        ├── NexusLogic.Plc.SiemensS7/         ← Siemens S7 드라이버
        ├── NexusLogic.Plc.AllenBradley/      ← Allen-Bradley EtherNet/IP 드라이버
        ├── NexusLogic.Plc.Hosting/           ← hosted service, health check
        └── NexusLogic.Plc.Simulator/         ← 드라이버 검증용 시뮬레이터
        ※ NexusLogic은 NexusCom과 sibling 서브모듈로 추가되었고($(NexusComRoot) 자동 해결),
          OPC-UA 설비 수집은 NexusLogic.Plc.OpcUa를 그대로 활용한다 (3.6 카테고리 2 참조).
          ※ SerialPort(RS-232/485)는 NexusLogic이 제공하지 않으므로 v2.0에서 범위 제외.
```

#### 솔루션 전체 구조

> **v2.0 주:** ①본 트리는 솔루션(가상 폴더) 구조다. **물리 배치**는 저장소 루트 기준
> `src/` 아래에 00.Main~04.Modules가 위치하고 `submodules/`·`db/`·`docs/`·`test/`는 루트 직속이다
> — 따라서 csproj 상대 참조는 `..\..\..\submodules\...` 3단계 상위가 된다 (3장 말미 csproj 예시 참조).
> ②**명명:** 본 문서의 `SmartEES.*`는 설계 명칭이며 구현 저장소는 `NexaOne.*` 접두를 사용한다
> (예: SmartEES.API → NexaOne.API, SmartEES.Infrastructure → NexaOne.Infrastructure,
> SmartEES.App(WinForms) → NexaOne.Server + NexaOne.Web(Blazor) — 6장 참조).

```
SmartEES.sln
│
├── submodules/                               ← ★ Git 서브모듈 (빌드 참조)
│   ├── NexusFramework/                       ← 워크플로우 엔진 + 설비 제어
│   └── NexusCom/                             ← DB 공급자 기반
│
├── 00.Main/
│   └── SmartEES.App                          ← WinForms 메인 애플리케이션
│
├── 01.Framework/                             ← SmartEES 전용 프레임워크 (NexusFramework 미포함 기능)
│   ├── Micube.Framework                      ← 핵심 유틸리티 (AppConfig, EventAggregator, Language, UserInfo)
│   ├── Micube.Framework.Net                  ← 네트워크/HTTP 통신 추상화
│   ├── Micube.Framework.Net.Http             ← HTTP 클라이언트 구현체
│   ├── Micube.Framework.SmartControls        ← UI 컨트롤 라이브러리 (WinForms 전용)
│   └── Micube.Framework.Log                  ← 로깅 인프라
│   ※ NexusFramework 참조: Workflow, PlantController, IoC → submodules 프로젝트 직접 참조
│
├── 02.Backend/
│   ├── SmartEES.API                          ← ASP.NET Core Web API 호스트
│   ├── SmartEES.Application                  ← 애플리케이션 서비스 (Use Cases + [WorkflowCallable] 메서드)
│   ├── SmartEES.Domain                       ← 도메인 모델 / 비즈니스 규칙
│   ├── SmartEES.Infrastructure               ← DB/외부시스템 인터페이스 + 공통 구현
│   └── SmartEES.Infrastructure.Messaging     ← Kafka 글루(NexusCom KafkaDriver 래핑) / SignalR
│
├── 03.Driver/                                ← ★ 드라이버 레이어 (교체 가능 외부 의존)
│   │
│   ├── 01.Db/                               ← [DB 드라이버] NexusCom IDatabaseProvider 기반
│   │   ├── (NexusCom.Data.MsSql)            ← ★ v2.0 서브모듈로 이관 완료 — MSSQL 공급자
│   │   ├── (NexusCom.Data.PostgreSql)       ← submodules 직접 사용 — PostgreSQL 공급자
│   │   ├── (NexusCom.Data.MySql)            ← submodules 직접 사용 — MySQL 공급자
│   │   └── SmartEES.Driver.Oracle           ← 별도 구현 — Oracle (NexusCom 없음, 선택)
│   │   ※ v2.0: DB 공급자는 전부 NexusCom 서브모듈 프로젝트를 직접 참조한다.
│   │     01.Db/ 물리 폴더는 솔루션 폴더(가상)로만 남아 공급자 프로젝트를 묶는다.
│   │
│   ├── 02.Communication/                    ← [통신 글루] SmartEES 전용 어댑터 + 과도기 구현 (v2.0)
│   │   ├── (NexusCom.Messaging.Kafka)       ← ★ v2.0 이관 완료 — Confluent.Kafka (기본, 서브모듈 직접 참조)
│   │   ├── (NexusCom.Messaging.RabbitMq)    ← 계획 — AMQP (선택, NexusCom에 직접 구현 예정)
│   │   ├── (NexusCom.Messaging.Mqtt)        ← 계획 — MQTT (선택, NexusCom에 직접 구현 예정)
│   │   ├── (NexusCom.Notify.SmtpEmail)      ← ★ v2.0 이관 완료 — SMTP 이메일 (기본, 서브모듈 직접 참조)
│   │   ├── (NexusCom.Notify.Sms)            ← 계획 — SMS (선택, NexusCom에 직접 구현 예정)
│   │   ├── (NexusCom.Directory.Ldap)        ← 계획 — LDAP/Active Directory (선택, NexusCom에 직접 구현 예정)
│   │   ├── (NexusLogic.Plc.OpcUa)           ← ★ NexusLogic 소속 (서브모듈 추가 완료) — OPC-UA 설비 수집, IPlcDriver
│   │   └── SmartEES.Driver.Adapters         ← 잔류 — IDeviceInterface ↔ IPlcDriver 어댑터 글루 (FDC 연동 시 구현)
│   │   ※ v2.0: 프로토콜 구현은 서브모듈(NexusCom/NexusLogic)이 소유한다.
│   │     이관 완료 전까지는 02.Communication/ 아래 과도기 구현이 임시로 존재할 수 있으며,
│   │     이관 즉시 서브모듈 프로젝트 참조로 대체하고 과도기 사본은 삭제한다.
│   │     ※ 메시징 글루(KafkaMessageBus/KafkaConsumerService)는 현재
│   │       02.Backend/SmartEES.Infrastructure.Messaging에 위치한다 (3.6.1 참조).
│   │
│   └── 03.Cache/                            ← [캐시 드라이버] 메모리 캐시
│       ├── SmartEES.Driver.Redis            ← Redis 분산 캐시 (기본)
│       └── SmartEES.Driver.MemoryCache      ← 인프로세스 캐시 (개발/단일 서버)
│
├── 04.Modules/                               ← ★ 도메인 모듈 레이어 (비즈니스 기능)
│   ├── Micube.SmartEES.Mdm                   ← 마스터 데이터 관리 (MDM)
│   ├── Micube.SmartEES.Ept                   ← 설비 성능 추적 (EPT)
│   ├── Micube.SmartEES.Fdc                   ← 설비 데이터 수집 (FDC — PlantController 활용)
│   ├── Micube.SmartEES.Rms                   ← 레시피 관리 (RMS)
│   ├── Micube.SmartEES.Qms                   ← 품질 관리 (QMS)
│   ├── Micube.SmartEES.Ems                   ← 설비 보전 (EMS)
│   ├── Micube.SmartEES.Ppm                   ← 생산 계획 (PPM)
│   ├── Micube.SmartEES.Dlv                   ← 배송 관리 (DLV)
│   └── Micube.SmartEES.SystemManagement      ← 시스템 관리
│
└── 05.Tests/
    ├── SmartEES.UnitTests
    ├── SmartEES.IntegrationTests
    └── SmartEES.UITests
```

### 3.2 레이어 의존성 규칙

```
SmartEES.App (WinForms 진입점)
    └─→ Micube.Framework.*
    └─→ Micube.Framework.Net.Http          (HttpChannel)
    └─→ 04.Modules/* DLL 동적 로딩         (FormCreator가 ./Modules/ 에서 로드)

04.Modules/* (각 도메인 모듈)               ← DB 드라이버 직접 참조 없음
    └─→ Micube.Framework.SmartControls
    └─→ Micube.Framework.Net.Http
    └─→ SmartEES.Application               (서비스 호출 — HTTP/API 경유)

SmartEES.API (Web API 호스트)
    └─→ SmartEES.Application
    └─→ SmartEES.Infrastructure            (IDatabaseProvider 인터페이스 참조)
    └─→ 03.Driver/01.Db/* 중 설정된 1개만   ← appsettings.json DbmsType 기반
    └─→ NexusFramework (WorkflowManager)   ← submodules 프로젝트 참조

SmartEES.Application                       ← [WorkflowCallable] 메서드 포함
    └─→ SmartEES.Domain
    └─→ NexusFramework.Workflow.*          ← WorkflowCallableAttribute, NodeContext

03.Driver/01.Db/* (각 DB 공급자)            ← 서로 의존 없음
    └─→ NexusCom.Data.Abstractions         (IDatabaseProvider 인터페이스 구현)
    └─→ DBMS별 NuGet 패키지만 참조
    ※ PostgreSQL/MySQL: NexusCom 서브모듈 프로젝트 직접 사용
    ※ MSSQL: NexusCom.Data.MsSql — v2.0 서브모듈로 이관 완료 (submodules/NexusCom/src/)
    ※ Oracle: SmartEES.Driver.Oracle 자체 구현

03.Driver/02.Communication/*               ← v2.0 프로토콜 구현은 서브모듈 소유
    └─→ NexusCom.Messaging.* / Notify.* / Directory.*  (Kafka·SmtpEmail — 이관 완료★ / RabbitMq·Mqtt·Sms·Ldap — 계획)
    └─→ NexusLogic.Plc.OpcUa               (OPC-UA 설비 프로토콜, IPlcDriver — v2.0 서브모듈 추가 완료)
    └─→ SmartEES.Infrastructure            (IMessageBrokerDriver/IEquipmentDriver 인터페이스 — 어댑터 글루만)
    └─→ NexusFramework.Resource.*          (IDeviceInterface — 어댑터가 NexusLogic IPlcDriver를 감싸 구현)
    ※ 이관 완료 전 과도기 구현은 02.Communication/에 임시 잔류 가능 (3.6 참조)

SmartEES.Infrastructure (인터페이스 + 공통)
    └─→ SmartEES.Application
    └─→ SmartEES.Domain
    └─→ NexusCom.Data.Abstractions         (IDatabaseProvider, IDriverManager 참조)

Micube.SmartEES.Fdc (FDC 모듈)             ← PlantController 활용
    └─→ NexusFramework                     (PlantController, Machine, IStateMachine)
    └─→ SmartEES.Application

SmartEES.Domain
    └─→ (외부 의존 없음)
```

**드라이버 DI 등록 (SmartEES.API/Program.cs):**
```csharp
// appsettings.json: "Database": { "DbmsType": "MSSQL" }
var dbmsType = builder.Configuration["Database:DbmsType"];

// NexusCom IDatabaseProvider 기반 등록
builder.Services.AddSmartEESDatabase(dbmsType, builder.Configuration);
// → "MSSQL"      : NexusCom.Data.MsSql.MsSqlProvider 등록 (v2.0 서브모듈 소속)
// → "PostgreSQL" : NexusCom.Data.PostgreSql.PostgreSqlProvider 등록
// → "MySQL"      : NexusCom.Data.MySql.MySqlProvider 등록
// → "Oracle"     : SmartEES.Driver.Oracle 자체 구현 등록

// NexusFramework WorkflowManager DI 등록
builder.Services.AddWorkflowEngine(options =>
{
    options.WorkflowDirectory = builder.Configuration["Workflow:Directory"];
});

// NexusCom DataHostedService (ChangeFeed 백그라운드)
builder.Services.AddNexusComHosting(builder.Configuration);
```

### 3.3 DB 드라이버 — NexusCom IDatabaseProvider 기반 설계

> **v1.8 변경:** 기존 자체 `IDbDriver` 인터페이스를 `submodules/NexusCom`의 `IDatabaseProvider` 패턴으로 대체한다.  
> NexusCom은 PostgreSQL/MySQL/SQLite 공급자를 이미 제공하며, **MSSQL은 없으므로 `NexusCom.Data.MsSql` 신규 구현**이 필요하다.  
> **v2.0 변경:** `NexusCom.Data.MsSql` 구현 후 NexusCom 서브모듈로 이관 완료 — 모든 DB 공급자가 서브모듈 소속으로 통일되었다.

#### NexusCom IDatabaseProvider 인터페이스 구조

```
IDatabaseProvider (NexusCom.Data.Abstractions.Interfaces)
 ├── Kind              → DatabaseProviderKind (MSSQL / PostgreSQL / MySQL / SQLite)
 ├── Name              → 공급자 이름 식별자
 ├── Capabilities      → 지원 기능 플래그 (ChangeFeed, BulkInsert 등)
 ├── CreateConnection  → DbConnection 생성
 ├── QueryExecutor     → IQueryExecutor (쿼리/스칼라/스트리밍 실행)
 ├── MetadataProvider  → IMetadataProvider (테이블/컬럼/인덱스 조회)
 ├── TransactionManager→ ITransactionManager (트랜잭션 범위 실행)
 └── ChangeFeedProvider→ IChangeFeedProvider (실시간 변경 구독)
```

#### NexusCom 핵심 인터페이스 발췌 (submodules/NexusCom/src/NexusCom.Data.Abstractions/Interfaces/Contracts.cs)

> 본 절은 SmartEES가 직접 사용하는 인터페이스만 발췌한 것이다. 실제 파일에는
> `IMetadataProvider`의 `GetDatabasesAsync`/`GetSchemasAsync`와
> `IOffsetStore`·`IRuleEvaluator`·`IChangeEventNormalizer`가 추가로 정의되어 있다 — 전체는 Contracts.cs 참조.

```csharp
// NexusCom.Data.Abstractions
namespace NexusCom.Data.Abstractions.Interfaces;

public interface IDatabaseProvider
{
    DatabaseProviderKind Kind { get; }
    string Name { get; }
    ProviderCapabilities Capabilities { get; }
    DbConnection CreateConnection(string connectionString);

    IQueryExecutor QueryExecutor { get; }
    IMetadataProvider MetadataProvider { get; }
    ITransactionManager TransactionManager { get; }
    IChangeFeedProvider ChangeFeedProvider { get; }
}

public interface IQueryExecutor
{
    Task<int> ExecuteNonQueryAsync(DatabaseEndpoint endpoint, QueryRequest request, CancellationToken ct = default);
    Task<object?> ExecuteScalarAsync(DatabaseEndpoint endpoint, QueryRequest request, CancellationToken ct = default);
    Task<QueryResult> ExecuteReaderAsync(DatabaseEndpoint endpoint, QueryRequest request, CancellationToken ct = default);
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamAsync(DatabaseEndpoint endpoint, QueryRequest request, CancellationToken ct = default);
}

public interface IMetadataProvider
{
    Task<IReadOnlyList<TableInfo>> GetTablesAsync(DatabaseEndpoint endpoint, string? schema = null, CancellationToken ct = default);
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(DatabaseEndpoint endpoint, string? schema, string table, CancellationToken ct = default);
    Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(DatabaseEndpoint endpoint, string? schema, string table, CancellationToken ct = default);
}

public interface ITransactionManager
{
    // 트랜잭션 범위 내에서 비즈니스 로직 실행 — SqlTxnContext가 이 패턴으로 구현됨
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        DatabaseEndpoint endpoint,
        Func<DbConnection, DbTransaction, Task<TResult>> action,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken ct = default);
}

public interface IChangeFeedProvider
{
    Task StartAsync(DatabaseEndpoint endpoint, IEnumerable<ChangeSubscription> subscriptions,
        Func<ChangeEvent, Task> onEvent, CancellationToken ct = default);
    Task StopAsync(string endpointId, CancellationToken ct = default);
}

public interface IDriverManager
{
    Task<QueryResult> QueryAsync(DatabaseEndpoint endpoint, QueryRequest request, CancellationToken ct = default);
    Task<int> ExecuteAsync(DatabaseEndpoint endpoint, QueryRequest request, CancellationToken ct = default);
    Task SubscribeAsync(ChangeSubscription subscription, Func<ChangeEvent, Task> handler, CancellationToken ct = default);
    Task UnsubscribeAsync(string subscriptionId, CancellationToken ct = default);
}
```

#### NexusCom 공급자 현황

| 공급자 | 프로젝트 | 상태 | 비고 |
|--------|----------|------|------|
| **PostgreSQL** | `NexusCom.Data.PostgreSql` | ✅ 완성 | LISTEN/NOTIFY ChangeFeed 지원 |
| **MySQL** | `NexusCom.Data.MySql` | ✅ 완성 | Trigger 기반 ChangeFeed 지원 |
| **SQLite** | `NexusCom.Data.Sqlite` | ✅ 완성 | Trigger 기반 ChangeFeed 지원 |
| **MSSQL** | `NexusCom.Data.MsSql` | ✅ **완성 (v2.0 이관)** | UPDATED_AT 워터마크 폴링 ChangeFeed, 서브모듈 소속 |
| **Oracle** | `SmartEES.Driver.Oracle` | ⚠️ 자체 구현 | NexusCom 없음, 선택 사항 |

#### SmartEES 전용 기능 보완 — INexaOneEESDbCapability (v2.0 개명)

NexusCom IDatabaseProvider는 범용 DB 추상화이므로 SmartEES 전용 SQL 방언(페이징, Upsert, 시퀀스 채번 등)은 별도 인터페이스로 보완한다.

> **v2.0 변경:** `ISmartEESDbCapability` → `INexaOneEESDbCapability`로 개명하고
> 정의 위치를 SmartEES.Infrastructure에서 **NexusCom.Data.Abstractions**로 흡수했다.
> SQL 방언은 DB 공급자와 같은 bounded context에 속하므로 공급자(MsSqlProvider 등)가
> 앱 저장소를 역참조하지 않고 이중 구현할 수 있어야 하기 때문이다.
> 앱 쪽(NexaOne.Infrastructure)에는 `global using` 별칭만 남긴다.

| 그룹 | 기능 항목 | 설명 |
|------|-----------|------|
| **① 채번 / 시퀀스** | `GetSequenceSql` | TXN_HIST_KEY Sequence SQL |
| | `GetCurrentTimeSql` | 서버 현재 시각 SQL |
| **② 쿼리 빌더** | `WrapPaged` | 페이징 SQL 래퍼 |
| | `NoLockHint` | 읽기 잠금 힌트 (MSSQL: WITH NOLOCK) |
| | `BuildUpsertSql` | MERGE / ON CONFLICT 분기 |
| | `BuildBatchInsertSql` | 복수 행 INSERT 생성 |
| | `WrapTempTable` | 임시 테이블 생성 SQL |
| | `NullCoalesceFn` | NULL 대체 함수명 |
| | `ConcatColumns` | 문자열 연결 연산자 |
| **③ 벌크 작업** | `BulkInsertAsync` | SqlBulkCopy / COPY 최적화 |
| **④ 진단** | `GetLongRunningSql` | 장기 실행 쿼리 조회 SQL |
| | `GetActiveSessionsSql` | 활성 세션 수 조회 SQL |

```csharp
// NexusCom.Data.Abstractions/Interfaces/Contracts.cs — SQL 방언 확장 (v2.0 흡수)
namespace NexusCom.Data.Abstractions.Interfaces;

public interface INexaOneEESDbCapability
{
    string NoLockHint { get; }
    string GetSequenceSql(string sequenceName);
    string WrapPaged(string baseSql, int offset, int limit);
    string BuildUpsertSql(string tableName, IReadOnlyList<string> keyColumns, IReadOnlyList<string> dataColumns);
    Task BulkInsertAsync(
        DbConnection connection,
        string tableName,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken ct = default);

    // SQL 방언 확장 — 채번/시각, 쿼리 빌더, 진단
    string GetCurrentTimeSql { get; }
    string NullCoalesceFn { get; }
    string ConcatColumns(string left, string right);
    string BuildBatchInsertSql(string tableName, IReadOnlyList<string> columns, int rowCount);
    string WrapTempTable(string tempTableName, string selectSql);
    string GetLongRunningSql { get; }
    string GetActiveSessionsSql { get; }
}
// 각 공급자는 IDatabaseProvider + INexaOneEESDbCapability를 함께 구현
// NexusCom.Data.MsSql.MsSqlProvider : IDatabaseProvider, INexaOneEESDbCapability  ← v2.0 12멤버 구현 완료
// NexusCom.Data.PostgreSql.PostgreSqlProvider : IDatabaseProvider, INexaOneEESDbCapability (확장 예정)
```

구현은 본 절을 다음과 같이 적응한다. 설계 표의 **12개 기능 항목을 INexaOneEESDbCapability에 전부 정의하고
MsSqlProvider가 전부 구현**한다(v2.0). 시그니처는 컬렉션을 `IReadOnlyList<string>`로 통일했고,
SQL 본문은 §3.4 DBMS별 비교표의 MSSQL 컬럼을 따른다. MsSqlProvider.BuildUpsertSql은 동시 업서트 시
MERGE race로 인한 PK 중복을 막기 위해 `MERGE INTO ... WITH (HOLDLOCK)`(SERIALIZABLE) 힌트를 포함한다.
PostgreSql 등 타 공급자는 INexaOneEESDbCapability를 구현하지 않으므로(MsSql 전용 확장) 인터페이스 확장의 영향을 받지 않는다.

### 3.4 DBMS별 기능 비교표

| 기능 항목 | MSSQL | PostgreSQL | Oracle | MySQL |
|-----------|-------|------------|--------|-------|
| **연결 클래스** | `SqlConnection` | `NpgsqlConnection` | `OracleConnection` | `MySqlConnection` |
| **HealthCheck SQL** | `SELECT 1` | `SELECT 1` | `SELECT 1 FROM DUAL` | `SELECT 1` |
| **Sequence 채번** | `NEXT VALUE FOR {seq}` | `NEXTVAL('{seq}')` | `{seq}.NEXTVAL FROM DUAL` | 채번 테이블 + `LAST_INSERT_ID()` |
| **현재 시각** | `GETDATE()` | `NOW()` | `SYSDATE` | `NOW()` |
| **페이징** | `OFFSET n ROWS FETCH NEXT m ROWS ONLY` | `LIMIT m OFFSET n` | `ROWNUM` / `FETCH FIRST` | `LIMIT m OFFSET n` |
| **읽기 힌트** | `WITH (NOLOCK)` | _(없음)_ | _(없음)_ | _(없음)_ |
| **파라미터 접두사** | `@` | `@` | `:` | `@` |
| **UPSERT** | `MERGE INTO ... USING ...` | `ON CONFLICT DO UPDATE` | `MERGE INTO ... USING ...` | `ON DUPLICATE KEY UPDATE` |
| **임시 테이블** | `#tableName` | `TEMPORARY TABLE` | `GLOBAL TEMPORARY TABLE` | `TEMPORARY TABLE` |
| **NULL 대체** | `ISNULL(a,b)` | `COALESCE(a,b)` | `NVL(a,b)` | `COALESCE(a,b)` |
| **문자열 연결** | `col1 + col2` | `col1 \|\| col2` | `col1 \|\| col2` | `CONCAT(col1,col2)` |
| **벌크 INSERT** | `SqlBulkCopy` | `NpgsqlBinaryImporter (COPY)` | `OracleBulkCopy` | `MySqlBulkCopy` |
| **테이블 존재 확인** | `INFORMATION_SCHEMA.TABLES` | `pg_tables` | `ALL_TABLES` | `INFORMATION_SCHEMA.TABLES` |
| **장기 쿼리 조회** | `sys.dm_exec_requests` | `pg_stat_activity` | `V$SESSION` | `information_schema.processlist` |

### 3.5 SqlTxnContext — NexusCom ITransactionManager 기반으로 재설계

> **v1.8 변경:** `IDbDriver.GetSequenceSql()` → `ISmartEESDbCapability.GetSequenceSql()` 사용.  
> NexusCom `ITransactionManager.ExecuteInTransactionAsync()` 패턴으로 트랜잭션 범위를 래핑한다.  
> **v2.0 변경:** SQL 방언 인터페이스는 `INexaOneEESDbCapability`로 개명 (3.3 참조). 아래 코드도 개명 반영.

```csharp
// SmartEES.Application/SqlTxnContext.cs
// NexusCom ITransactionManager를 통해 트랜잭션 범위 내에서 TXN_HIST_KEY 채번 및 DML 실행
public class SqlTxnContext
{
    private readonly INexaOneEESDbCapability _dialect;

    public string TxnHistKey { get; private set; } = string.Empty;
    public string UserId     { get; }
    public string PlantId    { get; }
    public DateTime TxnTime  { get; } = DateTime.UtcNow;

    public SqlTxnContext(INexaOneEESDbCapability dialect, string userId, string plantId)
    {
        _dialect = dialect;
        UserId   = userId;
        PlantId  = plantId;
    }

    // NexusCom ITransactionManager 패턴 — 트랜잭션 콜백 내에서 채번
    public async Task GenerateTxnHistKeyAsync(DbConnection conn, DbTransaction txn)
    {
        var sql = _dialect.GetSequenceSql("SEQ_TXN_HIST_KEY");
        TxnHistKey = (await conn.ExecuteScalarAsync<string>(sql, transaction: txn))!;
    }
}

// ServiceObjectProcessor — ITransactionManager 기반 트랜잭션 래핑
public class ServiceObjectProcessor
{
    private readonly ITransactionManager _txManager;
    private readonly INexaOneEESDbCapability _dialect;
    private readonly DatabaseEndpoint _endpoint;

    public async Task<int> InsertAsync(string objectId, Dictionary<string, object> values)
    {
        return await _txManager.ExecuteInTransactionAsync(_endpoint,
            async (conn, txn) =>
            {
                var ctx = new SqlTxnContext(_dialect,
                    UserInfo.Current.UserId, UserInfo.Current.PlantId);
                await ctx.GenerateTxnHistKeyAsync(conn, txn);

                InjectAuditFields(values, "INSERT");
                values["TXN_HIST_KEY"] = ctx.TxnHistKey;

                var objDef = await _soRepository.GetObjectDefinitionAsync(objectId);
                var sql    = _sqlBuilder.BuildInsert(objDef.TableName, values);
                var affected = await conn.ExecuteAsync(sql, values, txn);

                if (objDef.HistoryEnabled)
                    await CopyToHistoryAsync(ctx, conn, txn, objDef.TableName, values);

                return affected;
            });
    }
}
```

**NexusCom ITransactionManager 활용 흐름:**
```
ServiceObjectProcessor.InsertAsync()
  └─ ITransactionManager.ExecuteInTransactionAsync(endpoint, (conn, txn) => {
       SqlTxnContext.GenerateTxnHistKeyAsync(conn, txn)   ← INexaOneEESDbCapability.GetSequenceSql
       InjectAuditFields()                                ← CREATOR/CREATEDTIME 자동 주입
       conn.ExecuteAsync(sql, values, txn)                ← Dapper DML
       CopyToHistoryAsync(ctx, conn, txn, ...)            ← _HIST 자동 복사
     })
```

### 3.6 드라이버 카테고리 설계

드라이버는 **DB / 통신 / 캐시** 3개 카테고리로 구분한다.  
`03.Driver/` 하위 해당 카테고리 폴더에 위치한다.

```
카테고리        폴더               인터페이스 기반                    기본 구현 (v2.0 소유 주체)
──────────────────────────────────────────────────────────────────────────────
DB 드라이버     01.Db/             NexusCom IDatabaseProvider         PostgreSqlProvider (NexusCom)
                                   + INexaOneEESDbCapability          MsSqlProvider (NexusCom — v2.0 이관 완료)
통신 드라이버   02.Communication/  IMessageBrokerDriver               KafkaDriver (NexusCom.Messaging — 이관 완료 + 앱 측 글루가 래핑)
                                   IEquipmentDriver                   NexusLogic.Plc.OpcUa (IPlcDriver — 서브모듈, 어댑터 글루가 래핑)
                                   INotificationDriver                SmtpEmailDriver (NexusCom.Notify — 이관 완료 + 앱 측 글루가 래핑)
                                   IExternalAuthDriver                LdapDriver (NexusCom.Directory — 계획)
캐시 드라이버   03.Cache/          ICacheDriver                       RedisDriver (자체 구현)
──────────────────────────────────────────────────────────────────────────────
※ 파일 스토리지(IFileStorageDriver)와 DB 인증(DbAuthService)은
  프로토콜 드라이버가 아니므로 SmartEES.Infrastructure에 직접 구현
※ NexusCom DataHostedService가 DB ChangeFeed를 백그라운드로 처리
※ OPC-UA 설비 수집은 NexusLogic.Plc.OpcUa(IPlcDriver, v2.0 서브모듈 추가 완료)를 활용
  — 02.Communication/의 어댑터 글루가 NexusFramework.Resource.IDeviceInterface ↔ IPlcDriver를
    변환한다(FDC 연동 시 구현). SerialPort(RS-232/485)는 NexusLogic 미제공으로 v2.0 범위 제외.
```

---

## ── [카테고리 1] DB 드라이버 (`01.Db/`) ──────────────────────────

> **v1.8 변경:** NexusCom `IDatabaseProvider` + `ISmartEESDbCapability` 이중 구현 패턴.  
> PostgreSQL/MySQL은 NexusCom 서브모듈 프로젝트를 직접 사용 + `ISmartEESDbCapability` 확장 추가.  
> **v2.0 변경:** `NexusCom.Data.MsSql` 구현 완료 후 **NexusCom 서브모듈로 이관**
> (`submodules/NexusCom/src/NexusCom.Data.MsSql/`). SQL 방언 인터페이스는
> `INexaOneEESDbCapability`로 개명되어 NexusCom.Data.Abstractions에 정의된다 (3.3 참조).

#### NexusCom.Data.MsSql — MSSQL 공급자 (v2.0 서브모듈 소속)

아래 코드 블록은 **서브모듈에 실재하는 구현**이다(시그니처·SQL은 실제 파일과 일치).
INexaOneEESDbCapability 12개 멤버 전부를 MsSqlProvider가 구현한다 (v2.0).

```csharp
// submodules/NexusCom/src/NexusCom.Data.MsSql/MsSqlProvider.cs (v2.0 이관 완료 — 실제 구현)
namespace NexusCom.Data.MsSql;

public sealed class MsSqlProvider : IDatabaseProvider, INexaOneEESDbCapability
{
    public MsSqlProvider()   // 무인자 생성자 — 연결 문자열은 CreateConnection/DatabaseEndpoint 경유
    {
        Kind = DatabaseProviderKind.SqlServer;
        Name = "SQL Server";
        Capabilities = new ProviderCapabilities(
            SupportsTransactions: true,
            SupportsSchemas: true,
            SupportsNotifications: false,   // MSSQL은 LISTEN/NOTIFY 없음 → 워터마크 폴링으로 대체
            SupportsCdc: true,
            SupportsStreaming: true,
            SupportsBatching: true,
            SupportsPropertyBeforeAfter: true,
            SupportsServerSidePaging: true,
            SupportsParameterizedCommands: true);

        QueryExecutor = new MsSqlQueryExecutor();
        MetadataProvider = new MsSqlMetadataProvider();
        TransactionManager = new MsSqlTransactionManager();
        ChangeFeedProvider = new MsSqlChangeFeedProvider();
        // ChangeFeed: UPDATED_AT > @watermark 폴링 (기본 5초, polling_interval_ms 옵션),
        //   "_ID" 접미사 첫 컬럼을 Key로 사용, ChangeEvent(Source: "polling") 생성 — CDC는 선택
    }

    // ── IDatabaseProvider ──
    public DatabaseProviderKind Kind { get; }
    public string Name { get; }
    public ProviderCapabilities Capabilities { get; }
    public IQueryExecutor QueryExecutor { get; }
    public IMetadataProvider MetadataProvider { get; }
    public ITransactionManager TransactionManager { get; }
    public IChangeFeedProvider ChangeFeedProvider { get; }

    public DbConnection CreateConnection(string connectionString) =>
        new SqlConnection(connectionString);

    // ── INexaOneEESDbCapability (12개 멤버 전부 구현 — 3.3 계약 시그니처와 일치) ──

    public string NoLockHint => "WITH (NOLOCK)";

    public string GetSequenceSql(string sequenceName) =>
        $"SELECT NEXT VALUE FOR [{sequenceName}]";

    public string WrapPaged(string baseSql, int offset, int limit) =>
        $"{baseSql} ORDER BY (SELECT NULL) OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";

    public string BuildUpsertSql(
        string tableName,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> dataColumns)
    {
        var allColumns = keyColumns.Concat(dataColumns).ToList();
        var matchCond = string.Join(" AND ", keyColumns.Select(c => $"target.[{c}] = source.[{c}]"));
        var updateSet = string.Join(", ", dataColumns.Select(c => $"target.[{c}] = source.[{c}]"));
        var insertCols = string.Join(", ", allColumns.Select(c => $"[{c}]"));
        var insertVals = string.Join(", ", allColumns.Select(c => $"source.[{c}]"));

        // HOLDLOCK(SERIALIZABLE): 동시 업서트 시 MERGE race로 인한 PK 중복 오류 방지
        return $"""
            MERGE INTO [{tableName}] WITH (HOLDLOCK) AS target
            USING (VALUES ({string.Join(", ", allColumns.Select(c => $"@{c}"))}))
                AS source ({insertCols})
            ON ({matchCond})
            WHEN MATCHED THEN
                UPDATE SET {updateSet}
            WHEN NOT MATCHED THEN
                INSERT ({insertCols}) VALUES ({insertVals});
            """;
    }

    public async Task BulkInsertAsync(
        DbConnection connection,
        string tableName,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken ct = default)
    {
        // SqlBulkCopy 기반 — 행 딕셔너리 컬렉션을 DataTable로 변환 후 WriteToServerAsync
        // (상세 구현은 실제 파일 참조)
    }

    // ── SQL 방언 확장 (v2.0 구현 완료 — §3.4 MSSQL 컬럼) ──

    public string GetCurrentTimeSql => "GETDATE()";
    public string NullCoalesceFn => "ISNULL";
    public string ConcatColumns(string left, string right) => $"{left} + {right}";

    public string BuildBatchInsertSql(string tableName, IReadOnlyList<string> columns, int rowCount)
    {
        var colList = string.Join(", ", columns.Select(c => $"[{c}]"));
        var rows = Enumerable.Range(0, rowCount).Select(i =>
            $"({string.Join(", ", columns.Select(c => $"@{c}_{i}"))})");
        return $"INSERT INTO [{tableName}] ({colList}) VALUES {string.Join(", ", rows)}";
    }

    public string WrapTempTable(string tempTableName, string selectSql) =>
        $"SELECT * INTO #{tempTableName} FROM ({selectSql}) AS _t";

    public string GetLongRunningSql =>
        "SELECT r.session_id, r.status, r.command, " +
        "CAST(r.total_elapsed_time / 1000.0 AS DECIMAL(10,1)) AS elapsed_sec, t.text AS sql_text " +
        "FROM sys.dm_exec_requests r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t " +
        "WHERE r.total_elapsed_time / 1000 >= @thresholdSeconds";

    public string GetActiveSessionsSql =>
        "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1";
}
```

**DI 등록 (앱 측 글루 — 서브모듈에 없음, NexaOne.Infrastructure/API에서 구성):**
```csharp
// 서브모듈은 DI 패키지에 의존하지 않으므로 등록 확장은 앱 측에 둔다 (추가 예정)
public static IServiceCollection AddMsSqlProvider(this IServiceCollection services)
{
    var provider = new MsSqlProvider();   // 무인자 — 연결 문자열은 DatabaseEndpoint 경유
    services.AddSingleton<IDatabaseProvider>(provider);
    services.AddSingleton<INexaOneEESDbCapability>(provider);
    return services;
}
```

---

## ── [카테고리 2] 통신 드라이버 (`02.Communication/`) ─────────────

통신 드라이버는 **네트워크 프로토콜** 기반으로 외부 시스템과 데이터를 주고받는 모든 구현체를 포함한다.

> **v2.0 변경:** 프로토콜 구현은 SmartEES 자체 구현이 아니라 **서브모듈이 소유**한다.
> NexusCom 명명 규칙 `NexusCom.<BoundedContext>.<기술>`에 따라 메시징·알림·외부 인증
> bounded context로 분리하고, 설비 수집 프로토콜은 **NexusLogic**(구 NexusCom.Plc.* 이관처) 소속으로 정의한다.
> NexaMes `03.Driver/02.Communication/`에는 SmartEES 전용 어댑터 글루(IDeviceInterface ↔ NexusLogic IPlcDriver 등)와
> v2.0 기준 02.Communication/에는 **자체 프로토콜 구현이 더 이상 없다** — Kafka·SmtpEmail은 NexusCom 서브모듈로,
> OPC-UA는 NexusLogic.Plc.OpcUa(IPlcDriver)로 이관 완료되었고, SerialPort는 NexusLogic 미제공으로 범위 제외했다.
> NexaMes에는 SmartEES 전용 어댑터 글루(IDeviceInterface ↔ NexusLogic IPlcDriver 등)와 앱 측 소비 글루
> (NexaOne.Infrastructure.Messaging·NexaOne.API.Services 등)만 남는다.
> 아래 표의 인터페이스(IMessageBrokerDriver 등)와 코드 샘플은 계약 설계로서 유효하며,
> 구현 본체의 최종 소속 프로젝트만 변경된다.

| 드라이버 | 소속 프로젝트 (v2.0) | 프로토콜 | 용도 | 이관 상태 |
|----------|---------------------|---------|------|----------|
| `KafkaDriver` | NexusCom.Messaging.Kafka | Kafka 프로토콜 | 이벤트 발행·구독 | ✅ **이관 완료 (v2.0)** — 서브모듈 직접 참조 |
| `RabbitMqDriver` | NexusCom.Messaging.RabbitMq | AMQP | 메시지 브로커 (선택) | 계획 |
| `MqttDriver` | NexusCom.Messaging.Mqtt | MQTT | IoT 설비 수집 | 계획 |
| `SmtpEmailDriver` | NexusCom.Notify.SmtpEmail | SMTP | 이메일 알림 발송 | ✅ **이관 완료 (v2.0)** — 서브모듈 직접 참조 |
| `SmsDriver` | NexusCom.Notify.Sms | HTTP/API | SMS 발송 (선택) | 계획 |
| `LdapDriver` | NexusCom.Directory.Ldap | LDAP/LDAPS | AD/LDAP 외부 인증 | 계획 |
| `NexusLogic.Plc.OpcUa` | NexusLogic.Plc.OpcUa (서브모듈) | OPC-UA | 설비 데이터 수집 | ✅ **NexusLogic 활용 확정 (v2.0)** — IPlcDriver, 서브모듈 추가 완료 |
| ~~SerialPort~~ | — | RS-232/485 | 레거시 설비 수집 | ❌ **범위 제외 (v2.0)** — NexusLogic 미제공, NexaOne.Driver.SerialPort 삭제 |

---

#### 3.6.1 IMessageBrokerDriver (메시지 브로커)

```csharp
public interface IMessageBrokerDriver
{
    // 토픽에 메시지 발행 (FDC 수집, 설비 알람, 상태 변경)
    Task PublishAsync(string topic, string key, string payload,
        CancellationToken ct = default);

    // 토픽 구독 시작 (백그라운드 Hosted Service에서 호출)
    Task SubscribeAsync(string topic, string groupId,
        Func<string, string, Task> onMessage,
        CancellationToken ct = default);

    // 헬스 체크 (브로커 연결 가능 여부)
    Task<bool> HealthCheckAsync();
}
```

**KafkaDriver 구현 (v2.0 이관 완료 — 실제 구현):**

구현은 본 절을 다음과 같이 적응한다. 프로토콜 본체는 NexusCom 서브모듈의
`NexusCom.Messaging.Kafka`가 소유하며, **`IMessageBrokerDriver`를 직접 구현하지 않는
순수 드라이버**(Confluent.Kafka + `ILogger`만 의존, 앱 역참조 없음)로 유지한다.
현 구현의 앱 측 글루는 **`NexaOne.Infrastructure.Messaging`(02.Backend)** —
`KafkaMessageBus`(도메인 이벤트 발행, JSON 직렬화)와 `KafkaConsumerService`(BackgroundService
구독, 역직렬화·수동 커밋)가 KafkaDriver를 래핑한다. 위 `IMessageBrokerDriver` 계약 어댑터는
브로커 교체(RabbitMq 등)가 실제 요구될 때 이 글루를 일반화하여 도입한다 —
DB 드라이버의 DI 글루(카테고리 1)와 동일한 분리 원칙.

```csharp
// submodules/NexusCom/src/NexusCom.Messaging.Kafka/KafkaDriver.cs (v2.0 이관 완료 — 실제 구현)
namespace NexusCom.Messaging.Kafka;

public sealed class KafkaDriver : IDisposable
{
    private readonly ILogger<KafkaDriver> _logger;
    private IProducer<string, string>? _producer;
    private bool _disposed;

    public KafkaDriver(ILogger<KafkaDriver> logger)
    {
        _logger = logger;
    }

    public void Configure(string bootstrapServers, int? messageTimeoutMs = null)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,              // 전 복제본 승인 — 유실 방지
            EnableIdempotence = true      // 중복 발행 방지 (재시도 안전)
        };
        if (messageTimeoutMs is not null)
            config.MessageTimeoutMs = messageTimeoutMs;
        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task ProduceAsync(
        string topic, string key, string value, CancellationToken ct = default)
    {
        if (_producer is null) throw new InvalidOperationException("KafkaDriver not configured.");
        var result = await _producer.ProduceAsync(topic,
            new Message<string, string> { Key = key, Value = value }, ct);
        // ProduceException 발생 시 로깅 후 재throw — 호출부(어댑터)가 재시도 정책 결정
    }

    public IConsumer<string, string> CreateConsumer(
        string groupId,
        string bootstrapServers,
        int? sessionTimeoutMs = null,
        int? maxPollIntervalMs = null,
        Action<string>? onError = null)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false      // 수동 커밋 — 처리 완료 후 오프셋 확정
        };
        if (sessionTimeoutMs is not null)  config.SessionTimeoutMs  = sessionTimeoutMs;
        if (maxPollIntervalMs is not null) config.MaxPollIntervalMs = maxPollIntervalMs;

        var builder = new ConsumerBuilder<string, string>(config);
        if (onError is not null)
            builder.SetErrorHandler((_, e) => onError(e.Reason));
        return builder.Build();
    }

    public void Dispose()  // Flush(5초) 후 Producer 해제
    { /* 상세 구현은 실제 파일 참조 */ }
}
```

**앱 측 글루 (NexaOne.Infrastructure.Messaging — 구현 완료, KafkaDriver 래핑):**
```csharp
// NexaOne.Infrastructure.Messaging/KafkaMessageBus.cs — 도메인 이벤트 발행 글루 (실제 구현)
public sealed class KafkaMessageBus : IDisposable
{
    private readonly KafkaDriver _driver;   // NexusCom.Messaging.Kafka — 프로토콜 본체

    public KafkaMessageBus(string bootstrapServers, ILogger<KafkaMessageBus> logger, ILogger<KafkaDriver> driverLogger)
    {
        _driver = new KafkaDriver(driverLogger);
        _driver.Configure(bootstrapServers, messageTimeoutMs: 10_000);
    }

    public async Task PublishAsync(string topic, DomainEventMessage message, CancellationToken ct = default)
        => await _driver.ProduceAsync(topic, message.AggregateId, JsonSerializer.Serialize(message), ct);
    // KafkaConsumerService(BackgroundService)는 _driver.CreateConsumer(groupId, servers,
    //   sessionTimeoutMs: 10_000, maxPollIntervalMs: 300_000, onError: ...)로 구독 루프 구성
}

// IMessageBrokerDriver 계약 어댑터는 브로커 교체 요구 시 위 글루를 일반화하여 도입 (설계 보류)

// appsettings.json
// "Kafka": { "BootstrapServers": "localhost:9092" }
```

**Kafka 토픽 정의:**

| 토픽 | 발행자 | 구독자(SignalR Hub) | 설명 |
|------|--------|---------------------|------|
| `fdc.rawdata` | `FdcCollectorService` | `FdcHub` | FDC 수집 원시 데이터 |
| `equipment.state.changed` | `EquipmentService` | `EquipmentHub`, `AlarmHub` | 설비 상태 변경 |
| `equipment.alarm.fired` | `FdcInterlockService` | `AlarmHub` | 인터락·알람 발생 |
| `work.lot.tracked` | `LotTrackInService` | `WorkHub` | Lot TrackIn/Out |
| `deploy.version.changed` | `DeployService` | `DeployHub` | 클라이언트 배포 알림 |

---

---

## ── [카테고리 3] 캐시 드라이버 (`03.Cache/`) ─────────────────────

#### 3.6.2 ICacheDriver (캐시)

```csharp
public interface ICacheDriver
{
    // 값 조회 (없으면 null)
    Task<T> GetAsync<T>(string key);

    // 값 저장 (TTL 지정)
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null);

    // 키 삭제
    Task RemoveAsync(string key);

    // 키 존재 여부
    Task<bool> ExistsAsync(string key);

    // 패턴 삭제 (예: "menu:*" 전체 무효화)
    Task RemoveByPatternAsync(string pattern);
}
```

**캐시 적용 항목:**

| 캐시 키 패턴 | TTL | 내용 |
|-------------|-----|------|
| `auth:refresh:{token}` | 7일 | JWT Refresh Token (Rotation) |
| `menu:{userId}:{plantId}` | 30분 | 사용자 메뉴 트리 |
| `code:{codeClassId}:{lang}` | 1시간 | 공통 코드 목록 |
| `dict:{lang}` | 1시간 | 다국어 사전 전체 |
| `authority:{userId}` | 30분 | 사용자 권한 목록 |
| `equipment:state:{equipmentId}` | 실시간 | 설비 현재 상태 |

**RedisDriver 구현:**
```csharp
public class RedisDriver : ICacheDriver
{
    private readonly IDatabase _db;
    private readonly IServer  _server;

    public RedisDriver(IConnectionMultiplexer redis)
    {
        _db     = redis.GetDatabase();
        _server = redis.GetServer(redis.GetEndPoints().First());
    }

    public async Task<T> GetAsync<T>(string key)
    {
        var val = await _db.StringGetAsync(key);
        return val.IsNull ? default : JsonConvert.DeserializeObject<T>(val);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null)
        => await _db.StringSetAsync(key,
            JsonConvert.SerializeObject(value), ttl);

    public async Task RemoveAsync(string key)
        => await _db.KeyDeleteAsync(key);

    public async Task<bool> ExistsAsync(string key)
        => await _db.KeyExistsAsync(key);

    public async Task RemoveByPatternAsync(string pattern)
    {
        var keys = _server.Keys(pattern: pattern).ToArray();
        if (keys.Length > 0) await _db.KeyDeleteAsync(keys);
    }
}
```

**MemoryCacheDriver 구현 (개발 환경 / 단일 서버):**
```csharp
public class MemoryCacheDriver : ICacheDriver
{
    private readonly IMemoryCache _cache;
    private readonly HashSet<string> _keys = new();

    public async Task<T> GetAsync<T>(string key)
    {
        _cache.TryGetValue(key, out T val);
        return await Task.FromResult(val);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null)
    {
        _keys.Add(key);
        var opts = new MemoryCacheEntryOptions();
        if (ttl.HasValue) opts.SetAbsoluteExpiration(ttl.Value);
        _cache.Set(key, value, opts);
        await Task.CompletedTask;
    }

    public async Task RemoveAsync(string key)
    { _cache.Remove(key); _keys.Remove(key); await Task.CompletedTask; }

    public async Task<bool> ExistsAsync(string key)
        => await Task.FromResult(_cache.TryGetValue(key, out _));

    public async Task RemoveByPatternAsync(string pattern)
    {
        var regex = new System.Text.RegularExpressions.Regex(
            pattern.Replace("*", ".*"));
        var matched = _keys.Where(k => regex.IsMatch(k)).ToList();
        foreach (var k in matched) { _cache.Remove(k); _keys.Remove(k); }
        await Task.CompletedTask;
    }
}
```

---

> **[참고] 파일 스토리지 — SmartEES.Infrastructure로 배치**  
> 파일 저장/읽기는 네트워크 프로토콜이 아닌 파일 I/O이므로 드라이버 레이어가 아닌 `SmartEES.Infrastructure`에 `IFileStorageDriver` + `FileSystemDriver` / `BlobStorageDriver`로 직접 구현한다.  
> SYS_TB_FILE 연계: 저장된 경로를 `SYS_TB_FILE.FILE_PATH`에 기록. 삭제 순서: DB 레코드 먼저 → 파일 삭제.

---

#### 3.6.3 IEquipmentDriver (설비 통신 — FDC 전용)

```csharp
public interface IEquipmentDriver
{
    string ProtocolType { get; }  // "OPC-UA" | "MQTT"

    // 설비 연결
    Task ConnectAsync(string endpoint, CancellationToken ct = default);
    Task DisconnectAsync();
    bool IsConnected { get; }

    // 데이터 포인트 읽기 (단건)
    Task<EquipmentDataPoint> ReadAsync(string nodeId);

    // 복수 포인트 일괄 읽기
    Task<List<EquipmentDataPoint>> ReadBatchAsync(IEnumerable<string> nodeIds);

    // 실시간 구독 (값 변경 시 콜백)
    Task SubscribeAsync(IEnumerable<string> nodeIds,
        Action<EquipmentDataPoint> onDataReceived,
        int samplingIntervalMs = 1000);

    // 설비에 명령 쓰기 (레시피 다운로드, 상태 변경)
    Task WriteAsync(string nodeId, object value);
}

public class EquipmentDataPoint
{
    public string NodeId      { get; set; }
    public object Value       { get; set; }
    public DateTime Timestamp { get; set; }
    public string Quality     { get; set; }  // "Good" | "Bad" | "Uncertain"
}
```

**OPC-UA 활용 (v2.0 — NexusLogic.Plc.OpcUa 어댑터, 구현 완료):**

구현은 본 절을 다음과 같이 적응한다. OPC-UA 프로토콜 본체는 **NexusLogic 서브모듈의
`NexusLogic.Plc.OpcUa`**가 `IPlcDriver`로 제공한다(세션 팩토리·구독·재연결·품질상태·쓰기 승인
등 운영 기능 포함). NexaMes는 이를 직접 구현하지 않고, NexaOne.FDC 모듈의 **어댑터 글루**
`OpcUaDeviceInterface`가 NexusFramework `IDeviceInterface` 계약(Machine 장착용 lifecycle)을
`IPlcDriver`로 위임한다. 위 고수준 `IEquipmentDriver`는 FDC 수집 파이프라인이 `Connection`
(IPlcConnection의 Reader/Writer/SubscriptionProvider)을 통해 태그를 다룰 때의 설계 계약이다.

```csharp
// NexaOne.FDC/Infrastructure/Equipment/OpcUaDeviceInterface.cs (v2.0 구현 완료 — 실제 구현)
// IDeviceInterface로 NexusLogic.Plc.OpcUa(IPlcDriver)를 노출. PlantController의 Machine에 장착된다.
public sealed class OpcUaDeviceInterface : IDeviceInterface
{
    private readonly IPlcDriver _driver;     // 기본값: new NexusLogic.Plc.OpcUa.OpcUaDriver()
    private readonly PlcEndpoint _endpoint;
    private IPlcConnection? _connection;

    public string InterfaceName { get; }
    public ResourceState State { get; private set; } = ResourceState.Created;
    public IPlcConnection? Connection => _connection;   // 태그 읽기/쓰기/구독을 상위 수집 서비스에 노출
    public event EventHandler<DeviceErrorEventArgs>? ErrorOccurred;

    public async Task InitializeAsync(CancellationToken ct = default)   // ConnectAsync + OpenAsync → Ready
    {
        State = ResourceState.Initializing;
        _connection = await _driver.ConnectAsync(_endpoint, ct);
        await _connection.OpenAsync(ct);
        State = ResourceState.Ready;
    }

    public async Task StartAsync(CancellationToken ct = default)        // PingAsync → Running
    {
        await _connection!.PingAsync(ct);
        State = ResourceState.Running;
    }

    public async Task StopAsync(CancellationToken ct = default)         // CloseAsync → Stopped
    {
        if (_connection is not null) { State = ResourceState.Stopping; await _connection.CloseAsync(ct); }
        State = ResourceState.Stopped;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) { await _connection.DisposeAsync(); _connection = null; }
    }
    // 비 OPC-UA 엔드포인트 거부, 실패 시 State=Error + ErrorOccurred 전파 (상세는 실제 파일 참조)
}
```

> 어댑터의 lifecycle·상태 전이·에러 전파는 `OpcUaDeviceInterfaceTests`(8개)가 IPlcDriver 모킹으로 검증한다.

> **SerialPort(RS-232/485)는 v2.0에서 제외** — NexusLogic이 해당 프로토콜을 제공하지 않으며,
> NexaOne.Driver.SerialPort 자체 구현은 삭제했다. 레거시 시리얼 설비 연동이 실제로 필요해지면
> NexusLogic에 SerialPort 드라이버가 추가된 뒤 동일한 어댑터 패턴으로 도입한다.

---

#### 3.6.4 INotificationDriver (알림 발송)

```csharp
public interface INotificationDriver
{
    string ChannelType { get; }  // "Email" | "SMS" | "Push"

    // 알림 발송
    Task<bool> SendAsync(NotificationMessage message,
        CancellationToken ct = default);
}

public class NotificationMessage
{
    public string To          { get; set; }   // 수신자 (이메일/전화번호)
    public string Subject     { get; set; }   // 제목 (이메일 전용)
    public string Body        { get; set; }   // 본문
    public bool   IsHtml      { get; set; }   // HTML 포맷 여부
    public string TemplateId  { get; set; }   // 템플릿 ID (선택)
    public Dictionary<string, string> Variables { get; set; } // 템플릿 변수
}
```

**알림 사용 시나리오:**

| 시나리오 | 채널 | 수신자 |
|----------|------|--------|
| 비밀번호 초기화 | Email | `UserInfo.EmailAddress` |
| PM 예정일 도래 | Email | 담당자 |
| 인터락 발생 | Email + SMS | 엔지니어 |
| 레시피 승인 요청 | Email | 승인자 (`RMS_APPROVER1`) |
| 클라이언트 배포 완료 | Push(SignalR) | 전체 접속자 |

**SMTP 이메일 (v2.0 — NexusCom.Notify.SmtpEmail 활용):**

구현은 본 절을 다음과 같이 적응한다. SMTP 프로토콜 본체는 **NexusCom 서브모듈의
`NexusCom.Notify.SmtpEmail`**(MailKit 기반 `SmtpEmailDriver` — StartTls, 단건/복수 수신자, 첨부)이
소유한다. Kafka와 동일하게 **앱 역참조 없는 순수 드라이버**이며, 앱은 `IEmailSender` 계약으로만 소비한다.

```csharp
// submodules/NexusCom/src/NexusCom.Notify.SmtpEmail/SmtpEmailDriver.cs (v2.0 이관 완료 — 실제 구현)
namespace NexusCom.Notify.SmtpEmail;

public sealed class SmtpEmailDriver : IDisposable
{
    public void Configure(string host, int port, string userName, string password,
        string fromAddress, string fromDisplayName, bool useSsl = true) { /* ... */ }

    public async Task SendAsync(string to, string subject, string body, bool isHtml = false,
        IEnumerable<string>? cc = null,
        IEnumerable<(string FileName, Stream Content, string ContentType)>? attachments = null,
        CancellationToken ct = default) { /* MailKit SmtpClient — StartTls 연결 → 인증 → 전송 */ }

    public Task SendToManyAsync(IEnumerable<string> recipients, string subject, string body,
        bool isHtml = false, CancellationToken ct = default) { /* ... */ }
}

// 앱 측 글루: NexaOne.API/Services/SmtpEmailSender.cs — IEmailSender 구현, SmtpEmailDriver 래핑 (실제 구현)
public sealed class SmtpEmailSender : IEmailSender, IDisposable
{
    private readonly SmtpEmailDriver _driver;
    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailDriver> driverLogger)
    {
        _driver = new SmtpEmailDriver(driverLogger);
        var s = configuration.GetSection("Smtp");
        _driver.Configure(s["Host"] ?? "localhost", int.Parse(s["Port"] ?? "587"),
            s["UserName"] ?? "", s["Password"] ?? "",
            s["FromAddress"] ?? "noreply@nexaone.local", s["FromDisplayName"] ?? "NexaOne MES",
            bool.Parse(s["UseSsl"] ?? "true"));
    }
    public Task SendAsync(string to, string subject, string body, bool isHtml = false, CancellationToken ct = default)
        => _driver.SendAsync(to, subject, body, isHtml, ct: ct);
    public void Dispose() => _driver.Dispose();
}
// appsettings.json: "Smtp": { "Host": "mail.company.com", "Port": 587,
//   "UserName": "noreply@company.com", "Password": "***", "UseSsl": true }
```

---

#### 3.6.5 IExternalAuthDriver (외부 인증 — LDAP/AD)

> **[배치 원칙]**  
> - **DB 기반 인증(`DbAuthService`)** — `SYS_TB_USER`를 조회하는 로직은 `SmartEES.Infrastructure`에 직접 구현 (IDatabaseProvider/IDriverManager 사용, 별도 드라이버 불필요)  
> - **외부 디렉터리 인증(`LdapDriver`)** — LDAP/LDAPS 프로토콜로 외부 AD 서버에 연결하므로 통신 드라이버로 분류

```csharp
// 인터페이스는 외부 프로토콜 인증만 담당
public interface IExternalAuthDriver
{
    string AuthType { get; }  // "LDAP" | "SAML" | "OAuth2"

    // 외부 시스템에서 자격증명만 검증 (성공 여부 반환)
    // 권한·메뉴는 검증 후 SYS_TB_USER에서 별도 조회
    Task<bool> ValidateCredentialsAsync(string userId, string password);

    // 외부 디렉터리에서 사용자 기본 정보 가져오기 (이름, 이메일 등 동기화용)
    Task<ExternalUserInfo> GetUserInfoAsync(string userId);
}

public class ExternalUserInfo
{
    public string UserId      { get; set; }
    public string DisplayName { get; set; }
    public string Email       { get; set; }
    public string Department  { get; set; }
}
```

**LdapDriver 구현 (NexusCom.Directory.Ldap — v2.0 계획, NexusCom에 직접 구현 예정):**
```csharp
// NuGet: Novell.Directory.Ldap.NETStandard
// appsettings.json: "Ldap": { "Host": "ad.company.com", "Port": 636,
//   "Domain": "company.com", "BaseDn": "DC=company,DC=com", "UseSsl": true }
public class LdapDriver : IExternalAuthDriver
{
    public string AuthType => "LDAP";
    private readonly LdapOptions _options;

    public async Task<bool> ValidateCredentialsAsync(string userId, string password)
    {
        try
        {
            using var conn = new LdapConnection();
            conn.SecureSocketLayer = _options.UseSsl;
            conn.Connect(_options.Host, _options.Port);
            conn.Bind($"{userId}@{_options.Domain}", password);  // AD Bind 인증
            return await Task.FromResult(true);
        }
        catch (LdapException) { return false; }
    }

    public async Task<ExternalUserInfo> GetUserInfoAsync(string userId)
    {
        using var conn = new LdapConnection();
        conn.Connect(_options.Host, _options.Port);
        conn.Bind(_options.ServiceAccount, _options.ServicePassword);
        var results = conn.Search(_options.BaseDn, LdapConnection.ScopeSub,
            $"(sAMAccountName={userId})",
            new[] { "displayName", "mail", "department" }, false);
        var entry = results.Next();
        return await Task.FromResult(new ExternalUserInfo
        {
            UserId      = userId,
            DisplayName = entry.GetAttribute("displayName")?.StringValue,
            Email       = entry.GetAttribute("mail")?.StringValue,
            Department  = entry.GetAttribute("department")?.StringValue
        });
    }
}
```

> **혼합 인증 흐름:**  
> 1. `AuthController` → `IExternalAuthDriver.ValidateCredentialsAsync()` (LDAP 자격증명 확인)  
> 2. 성공 시 → `DbAuthService.GetUserWithAuthoritiesAsync()` (SYS_TB_USER 권한 조회)  
> 3. LDAP는 ID/PW 검증만, 메뉴·권한·플랜트는 SmartEES DB가 관리

#### SmartEES.App.csproj
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <RootNamespace>SmartEES</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DevExpress.Win" Version="24.1.*" />
    <PackageReference Include="Ninject" Version="4.0.*" />
    <PackageReference Include="Newtonsoft.Json" Version="13.*" />
    <PackageReference Include="YamlDotNet" Version="15.*" />
  </ItemGroup>
  <ItemGroup>
    <!-- 프로젝트는 00.Main/SmartEES.App/에 위치 — 01.Framework까지 2단계 상위 -->
    <ProjectReference Include="..\..\01.Framework\Micube.Framework\Micube.Framework.csproj" />
    <ProjectReference Include="..\..\01.Framework\Micube.Framework.Net.Http\Micube.Framework.Net.Http.csproj" />
    <!-- 모듈은 DLL로 동적 로드 — 빌드 시 참조 없음 -->
  </ItemGroup>
</Project>
```
> ※ v1.9에서 UI가 Blazor Server로 재설계되어 WinForms 클라이언트(SmartEES.App)와
> 01.Framework/Micube.*는 **구현 저장소에 존재하지 않는다** — 본 csproj는 설계 이력 참고용.

#### SmartEES.API.csproj
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>SmartEES.API</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <!-- 인증/보안 -->
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.*" />
    <!-- 쿼리 실행 (드라이버 NuGet은 각 Driver 프로젝트에 격리) -->
    <PackageReference Include="Dapper" Version="2.*" />
    <!-- 메시지: Confluent.Kafka는 NexusCom.Messaging.Kafka(서브모듈) 소유 —
         Infrastructure.Messaging의 ProjectReference로 전이 참조 (v2.0, API 직접 참조 금지) -->
    <PackageReference Include="Microsoft.AspNetCore.SignalR" Version="8.*" />
    <!-- API 문서 -->
    <PackageReference Include="Swashbuckle.AspNetCore" Version="6.*" />
    <!-- 캐시 -->
    <PackageReference Include="StackExchange.Redis" Version="2.*" />
    <PackageReference Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="8.*" />
    <!-- 로깅 -->
    <PackageReference Include="Serilog.AspNetCore" Version="8.*" />
    <PackageReference Include="Serilog.Sinks.File" Version="5.*" />
    <PackageReference Include="Serilog.Sinks.Console" Version="4.*" />
    <!-- 헬스체크 -->
    <PackageReference Include="AspNetCore.HealthChecks.Redis" Version="8.*" />
    <!-- 직렬화 -->
    <PackageReference Include="Newtonsoft.Json" Version="13.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\SmartEES.Application\SmartEES.Application.csproj" />
    <ProjectReference Include="..\SmartEES.Infrastructure\SmartEES.Infrastructure.csproj" />
    <ProjectReference Include="..\SmartEES.Infrastructure.Messaging\SmartEES.Infrastructure.Messaging.csproj" />
    <!-- DB 드라이버: NexusCom IDatabaseProvider 기반 조건부 참조 (v2.0: MsSql도 서브모듈 경로) -->
    <!-- 경로 깊이: 프로젝트는 src/02.Backend/<프로젝트>/에 있으므로 저장소 루트의 submodules/까지 3단계 상위 -->
    <ProjectReference Include="..\..\..\submodules\NexusCom\src\NexusCom.Data.MsSql\NexusCom.Data.MsSql.csproj"
                      Condition="'$(DbmsType)'=='MSSQL' or '$(DbmsType)'==''" />
    <ProjectReference Include="..\..\..\submodules\NexusCom\src\NexusCom.Data.PostgreSql\NexusCom.Data.PostgreSql.csproj"
                      Condition="'$(DbmsType)'=='PostgreSQL'" />
    <ProjectReference Include="..\..\..\submodules\NexusCom\src\NexusCom.Data.MySql\NexusCom.Data.MySql.csproj"
                      Condition="'$(DbmsType)'=='MySQL'" />
  </ItemGroup>
</Project>
```

#### NexusCom.Data.MsSql.csproj (v2.0 — NexusCom 서브모듈 소속)
```xml
<!-- submodules/NexusCom/src/NexusCom.Data.MsSql/NexusCom.Data.MsSql.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>NexusCom.Data.MsSql</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" Version="5.*" />
    <PackageReference Include="Dapper" Version="2.*" />
  </ItemGroup>
  <ItemGroup>
    <!-- NexusCom 내부 상대 참조만 사용 — 앱(SmartEES/NexaOne) 역참조 금지 -->
    <ProjectReference Include="..\NexusCom\NexusCom.csproj" />
    <ProjectReference Include="..\NexusCom.Data.Abstractions\NexusCom.Data.Abstractions.csproj" />
    <ProjectReference Include="..\NexusCom.Data.Core\NexusCom.Data.Core.csproj" />
  </ItemGroup>
</Project>
```
> **v2.0:** SQL 방언 인터페이스(INexaOneEESDbCapability)가 NexusCom.Data.Abstractions로
> 흡수되었으므로 SmartEES.Infrastructure 역참조가 제거되었다. 서브모듈은 앱 저장소에
> 의존하지 않는 순수 라이브러리로 유지한다.

#### NexusCom.Data.PostgreSql.csproj (서브모듈 소속 — 직접 사용)
```xml
<!-- submodules/NexusCom/src/NexusCom.Data.PostgreSql/NexusCom.Data.PostgreSql.csproj -->
<!-- v2.0: 별도 SmartEES.Driver.PostgreSQL 래퍼 프로젝트는 폐기 — 서브모듈 프로젝트를 직접 참조한다.
     MsSql과 동일하게 NexusCom 내부 상대 참조만 사용하며 앱(SmartEES/NexaOne) 역참조는 금지. -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>NexusCom.Data.PostgreSql</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Npgsql" Version="8.0.3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\NexusCom\NexusCom.csproj" />
    <ProjectReference Include="..\NexusCom.Data.Abstractions\NexusCom.Data.Abstractions.csproj" />
    <ProjectReference Include="..\NexusCom.Data.Core\NexusCom.Data.Core.csproj" />
  </ItemGroup>
</Project>
<!-- ※ 발췌 — 실제 파일의 ImplicitUsings/Nullable 등 공통 프로퍼티는 생략 -->
```

#### Micube.Framework.Net.Http.csproj
```xml
<!-- WinForms 클라이언트 → API 서버 HTTP 통신 레이어 -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <RootNamespace>Micube.Framework.Net.Http</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.*" />
    <PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" Version="7.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Micube.Framework\Micube.Framework.csproj" />
  </ItemGroup>
</Project>
```

> **역할:** `AppConfiguration`의 `Network.Main.Assembly: Micube.Framework.Net.Http`로 지정된 `HttpChannel` 구현체를 제공. Access Token 만료 시 Refresh Token 재발급 흐름 자동 처리.

#### Micube.SmartEES.Mdm.csproj (도메인 모듈 대표 예시)
```xml
<!-- 04.Modules 내 모든 도메인 모듈의 공통 구조 -->
<!-- DB 드라이버를 직접 참조하지 않음 — API 서버 HTTP 경유 -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <RootNamespace>Micube.SmartEES.Mdm</RootNamespace>
    <!-- 빌드 출력을 App의 ./Modules/ 폴더로 복사 -->
    <OutputPath>..\..\00.Main\SmartEES.App\bin\$(Configuration)\Modules\</OutputPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DevExpress.Win" Version="24.1.*" />
    <PackageReference Include="Newtonsoft.Json" Version="13.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\01.Framework\Micube.Framework\Micube.Framework.csproj" />
    <ProjectReference Include="..\..\01.Framework\Micube.Framework.SmartControls\Micube.Framework.SmartControls.csproj" />
    <ProjectReference Include="..\..\01.Framework\Micube.Framework.Net.Http\Micube.Framework.Net.Http.csproj" />
    <!-- Application 레이어 참조 없음 — API 서버에 HTTP 요청만 전송 -->
  </ItemGroup>
</Project>
```

---

## 4. 프레임워크 레이어 상세 설계

### 4.1 Micube.Framework

현행 코드를 .NET 8로 업그레이드하며 핵심 구조 유지.

#### 4.1.1 AppConfiguration (YAML 설정 관리)

```csharp
// 현행 구조 유지, .NET 8 호환
namespace Micube.Framework
{
    public static class AppConfiguration
    {
        private static YamlMappingNode _rootNode;

        public static void Initialize(string configPath = "App.yml")
        {
            // App.yml 파일 로딩
        }

        public static string GetString(string name, string defaultValue = null)
        public static int GetInteger(string name, int defaultValue = 0)
        public static bool GetBoolean(string name, bool defaultValue = false)
        public static T GetEnum<T>(string name, T defaultValue = default) where T : Enum
        public static IConvertible[] GetArray(string name)
        public static Dictionary<TKey, TValue> GetDictionary<TKey, TValue>(string name)
        // 점 표기법 경로 지원: "Application.Uiid", "Network.Main.Type"
    }
}
```

**App.yml 구조 (현행 유지):**
```yaml
Application:
  Uiid: SmartEES
  Language: ko-KR
  Plant: DEFAULT

Network:
  Main:
    Assembly: Micube.Framework.Net.Http
    Type: HttpChannel
    Url: http://localhost:8080

DLL:
  Path: ./Modules/
```

#### 4.1.2 EventAggregator (이벤트 버스)

```csharp
// 현행 구조 유지, Thread-safe Pub/Sub
namespace Micube.Framework.EventAggregator
{
    public class EventAggregator
    {
        public static EventAggregator Current { get; } = new EventAggregator();
        
        // WeakReference 기반 구독 (메모리 누수 방지)
        public Subscription<TMessageType> Subscribe<TMessageType>(Action<TMessageType> action)
        public void Publish<TMessageType>(TMessageType message)
        public void UnSubscribe(object target)
        public void UnSubscribe<TMessageType>(Subscription<TMessageType> subscription)
    }
    
    public class Subscription<TMessage> : IDisposable
    {
        private readonly WeakReference _weakReference;
        // WeakReference 기반 구독자 유지
    }
}
```

**이벤트 정의 목록:**

| 이벤트 클래스 | 용도 |
|--------------|------|
| `LanguageChangedEventArgs` | 언어 변경 브로드캐스트 |
| `PostEventArgs` | 범용 폼 간 데이터 전달 |
| `MenuOpenEventArgs` | 메뉴 열기 요청 |
| `FavoriteChangedEventArgs` | 즐겨찾기 변경 알림 |
| `UserLoginEventArgs` | 로그인/로그아웃 알림 |

#### 4.1.3 Language (다국어 관리)

```csharp
namespace Micube.Framework.Languages
{
    public static class Language
    {
        public static LanguageStore<LanguageItem> Dictionary { get; }
        public static LanguageStore<LanguageMessageItem> Message { get; }
        public static List<LanguageType> LanguageTypes { get; set; }
        public static string LanguageType { get; private set; } = "ko-KR";

        public static string Get(string id, params string[] args)
        public static void ChangeLanguage(string languageType)
        // EventAggregator를 통해 LanguageChangedEventArgs 발행
    }
}
```

**지원 언어 목록 (5개):**

| 코드 | 언어 | 비고 |
|------|------|------|
| `ko-KR` | 한국어 | 기본값 |
| `en-US` | 영어 | |
| `zh-CN` | 중국어 간체 | |
| `vi-VN` | 베트남어 | |
| `lo-LO` | 라오어 | 동남아 현장 지원 |

> **⚠️ 실제 소스 확인 사항:** `lo-LO` 라오어는 `SYS_TB_DICTIONARY`에 `LANGUAGE_TYPE = 'lo-LO'` 레코드로 저장되며, App.yml `Language: lo-LO` 설정 시 활성화된다. 번역 미등록 키는 `ko-KR` fallback.

```csharp
namespace Micube.Framework.Languages
{
    public class LanguageItem
    {
        public string ItemId { get; set; }
        public string Name { get; set; }
        public string LanguageType { get; set; }
        public string Description { get; set; }
    }

    public class LanguageMessageItem
    {
        public string ItemId { get; set; }
        public string Message { get; set; }
        public string Title { get; set; }
        public string LanguageType { get; set; }
    }
}
```

#### 4.1.4 UserInfo (사용자 정보)

> **⚠️ 실제 소스 확인 사항:** `UserInfo`는 `static class`가 아니라 **`UserInfo.Current` 싱글톤** 패턴이다.  
> 또한 `EmailAddress`, `CellPhoneNumber`, `Department`, `GetPlantStartBusinessHour()`, `IsAuthenticated` 속성이 추가로 존재한다.

```csharp
namespace Micube.Framework
{
    // 싱글톤 인스턴스 — static 클래스가 아님
    public class UserInfo
    {
        // ───── 싱글톤 접근점 ─────────────────────────
        private static UserInfo _current = new UserInfo();
        public static UserInfo Current => _current;

        // ───── 기본 인증 정보 ─────────────────────────
        public string UserId        { get; set; }
        public string UserName      { get; set; }
        public string PlantId       { get; set; }
        public string LanguageType  { get; set; }
        public string Uiid          { get; set; }   // 현재 열린 화면 ID
        public string ConnectionKey { get; set; }   // TXN_HIST_KEY (로그인 트랜잭션 키)

        // ───── 추가 사용자 속성 (실제 소스 확인) ────────
        public string EmailAddress      { get; set; }
        public string CellPhoneNumber   { get; set; }
        public string Department        { get; set; }

        // ───── 권한 목록 ──────────────────────────────
        public List<string> AuthorityList { get; set; } = new List<string>();

        // ───── 인증 상태 ──────────────────────────────
        public bool IsAuthenticated { get; private set; }

        // ───── 공장 운영 시간 조회 ────────────────────
        // 플랜트별 근무 시작 시간(Hour)을 반환. 기본값 0.
        public int GetPlantStartBusinessHour() { ... }

        // ───── 세션 초기화 ────────────────────────────
        // 로그인 성공 후 Set(), 로그아웃/강제종료 시 Clear()
        public static void Set(UserInfoDto dto)
        {
            Current.UserId          = dto.UserId;
            Current.UserName        = dto.UserName;
            Current.PlantId         = dto.PlantId;
            Current.LanguageType    = dto.LanguageType;
            Current.ConnectionKey   = dto.ConnectionKey;
            Current.EmailAddress    = dto.EmailAddress;
            Current.CellPhoneNumber = dto.CellPhoneNumber;
            Current.Department      = dto.Department;
            Current.AuthorityList   = dto.AuthorityList ?? new List<string>();
            Current.IsAuthenticated = true;
        }

        public static void Clear()
        {
            _current = new UserInfo();   // 인스턴스 교체로 완전 초기화
        }
    }
}
```

**사용 패턴 (기존 `UserInfo.UserId` → `UserInfo.Current.UserId` 전환):**

```csharp
// FrameworkSettings.InitializeMessage 내부
NetworkSettings.MessageSettings += (msg) =>
{
    msg.Head.UserId             = UserInfo.Current.UserId;
    msg.Head.Uiid               = UserInfo.Current.Uiid;
    msg.Transaction.LanguageType = UserInfo.Current.LanguageType;
    msg.Transaction.PlantId     = UserInfo.Current.PlantId;
};

// 로그인 성공 처리 (LoginForm 내부)
UserInfo.Set(loginResponse.UserInfo);

// 로그아웃 처리 (MainForm 내부)
UserInfo.Clear();
```

#### 4.1.5 Cryptography (암호화)

```csharp
namespace Micube.Framework
{
    public static class Cryptography
    {
        // SHA256 기반 패스워드 해시
        public static string Hash(string input)
        
        // AES 암호화/복호화 (설정 값 보호)
        public static string Encrypt(string plainText)
        public static string Decrypt(string cipherText)
    }
}
```

### 4.2 Micube.Framework.Net

네트워크 통신 레이어. WCF 대신 HttpClient 기반으로 현대화.

#### 4.2.1 MessageWorker

```csharp
namespace Micube.Framework.Net
{
    public class MessageWorker
    {
        public string RuleName { get; set; }
        public IMessageSerializer Serializer { get; set; }
        public IMessageChannel MessageChannel { get; set; }
        
        // 메시지 헤더 (IP, Timeout, UserContext 자동 주입)
        public MessageHead Head { get; }
        public MessageTransaction Transaction { get; }

        public MessageWorker SetTimeOut(TimeSpan? timeout)
        public MessageWorker SetBody(IMessageBody body)
        public MessageWorker SetBody(string key, object data)

        public IResponse<T> Execute<T>()
        public Task<IResponse<T>> ExecuteAsync<T>(CancellationToken ct = default)
    }
}
```

#### 4.2.2 SqlExecuter

```csharp
namespace Micube.Framework.Net
{
    public static class SqlExecuter
    {
        // 쿼리 실행 (ID 기반, Config/Query/xml 에서 SQL 로딩)
        public static DataTable Query(string id, string version,
            Dictionary<string, object> param = null)
        public static Task<DataTable> QueryAsync(string id, string version,
            Dictionary<string, object> param = null)

        // 저장 프로시저 실행
        public static DataTable Procedure(string name,
            Dictionary<string, object> param = null,
            TimeSpan? timeOut = null)
        public static Task<DataTable> ProcedureAsync(string name,
            Dictionary<string, object> param = null)

        // DataSet 반환 (멀티 결과셋)
        public static DataSet ProcedureToDataSet(string name,
            Dictionary<string, object> param = null)
    }
}
```

#### 4.2.3 ChannelProxy (전송 레이어 팩토리)

```csharp
namespace Micube.Framework.Net
{
    public static class ChannelProxy
    {
        public static IMessageChannel MessageChannel { get; private set; }

        // App.yml의 Network.Main 설정으로 동적 로드
        public static void Initialize()
        public static string SendMessage(string message)
    }
    
    // HTTP 기본 채널 구현
    public class HttpChannel : IMessageChannel
    {
        private readonly HttpClient _client;
        
        public string SendMessage(string message)
        // POST /do 엔드포인트로 JSON 전송
    }
}
```

#### 4.2.4 메시지 데이터 구조

```csharp
namespace Micube.Framework.Net.Data
{
    public class MessageObject
    {
        public MessageHead Head { get; set; }
        public IMessageBody Body { get; set; }
        public MessageTransaction Transaction { get; set; }
    }

    public class MessageHead
    {
        public string RuleName { get; set; }
        public string UserId { get; set; }
        public string Uiid { get; set; }
        public string IpAddress { get; set; }
        public int Timeout { get; set; }
    }

    public class MessageTransaction
    {
        public string ConnectionKey { get; set; }
        public string LanguageType { get; set; }
        public string PlantId { get; set; }
    }

    // 일반 메시지 바디 (Dictionary<string, object>)
    public class MessageBody : Dictionary<string, object>, IMessageBody { }

    // 쿼리 전용 바디 (파라미터 토큰 포함)
    public class MessageQueryBody : Dictionary<string, QueryBodyItemToken>, IMessageBody { }

    public class QueryBodyItemToken
    {
        public string Key { get; set; }
        public object Value { get; set; }
        public ExecuteTypes ExecuteType { get; set; }
    }
    
    public enum ExecuteTypes { Query, Procedure, QueryForDataset }
}
```

### 4.3 Micube.Framework.Log

```csharp
namespace Micube.Framework.Log
{
    public static class Logger
    {
        // Microsoft.Extensions.Logging 기반으로 업그레이드
        public static void Info(string message)
        public static void Warn(string message, Exception ex = null)
        public static void Error(string message, Exception ex = null)
        public static void Debug(string message)
        
        // 감사 로그 (사용자 행동 추적)
        public static void Audit(string userId, string action, string target)
    }
}
```

---

## 5. 백엔드 서비스 레이어 설계 (Java → C# 전환)

현행 Java OSGi 기반 백엔드를 ASP.NET Core 기반 C# 서비스로 완전 전환.

### 5.1 아키텍처 개요

```
Client (WinForms / Web)
        │
        ▼ HTTP/JSON
┌─────────────────────────────────────────────┐
│         SmartEES.API (ASP.NET Core 8)        │
│  ┌───────────┐  ┌───────────┐  ┌──────────┐ │
│  │ REST API  │  │ SignalR   │  │ Swagger  │ │
│  │ /api/v1   │  │ /hubs/ees │  │ /docs    │ │
│  └─────┬─────┘  └─────┬─────┘  └──────────┘ │
└────────┼──────────────┼──────────────────────┘
         │              │
         ▼              ▼
┌─────────────────────────────────────────────┐
│    SmartEES.Application (Use Cases)          │
│  - RuleExecutor (현행 Java Rule 대체)          │
│  - ServiceObjectProcessor (현행 SO 패턴)       │
│  - WorkflowEngine                            │
└─────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────┐
│         SmartEES.Domain                      │
│  - Equipment, Lot, Process, Product 등       │
│  - 비즈니스 규칙 (순수 C# 클래스)               │
└─────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────┐
│      SmartEES.Infrastructure                 │
│  - Dapper 기반 DB 접근                        │
│  - QueryRepository (xml 쿼리 로딩)            │
│  - Kafka 메시지 발행                          │
└─────────────────────────────────────────────┘
```

### 5.2 SmartEES.API - 엔드포인트 설계

#### 5.2.0 인증 API (/api/auth/*)

> 상세 흐름은 섹션 9.1 및 섹션 19.1 참조.

```csharp
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    // POST /api/auth/login
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    // → 사용자 검증 → JWT 발급 → SaveConnectionHistory → UserInfoDto 반환

    // POST /api/auth/logout
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest req)
    // → Refresh Token 무효화 → SetLogoutTime()

    // POST /api/auth/refresh
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest req)
    // → Refresh Token 검증 → Token Rotation → 신규 토큰 반환

    // POST /api/auth/change-password
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
    // → 비밀번호 복잡도 검증 → SHA256 해시 → 이력 저장

    // POST /api/auth/reset-password
    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req)
    // → 이메일 인증 토큰 발송 → 토큰 검증 후 임시 비밀번호 발급

    // GET /api/auth/me
    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    // → 현재 로그인 사용자 정보 + 권한 목록 반환
}
```

#### 5.2.1 메인 비즈니스 API (현행 /do 엔드포인트 대체)

```csharp
// POST /api/v1/rule/{ruleName}
// 현행 Java의 모든 비즈니스 룰 호출을 단일 엔드포인트로 처리
[ApiController]
[Route("api/v1")]
[Authorize]                          // ← 전체 컨트롤러에 JWT 인증 필수
[Produces("application/json")]
public class RuleController : ControllerBase
{
    // POST /api/v1/rule/{ruleName}
    // Body: RuleRequest (Head.RuleName, Body, Transaction)
    [HttpPost("rule/{ruleName}")]
    public async Task<ActionResult<RuleResponse<object>>> Execute(
        string ruleName,
        [FromBody] RuleRequest request)

    // POST /api/v1/query  — 현행 SqlExecuter.Query 대체
    [HttpPost("query")]
    public async Task<ActionResult<RuleResponse<DataTable>>> ExecuteQuery(
        [FromBody] QueryRequest request)

    // POST /api/v1/procedure  — 현행 SqlExecuter.Procedure 대체
    [HttpPost("procedure")]
    public async Task<ActionResult<RuleResponse<DataTable>>> ExecuteProcedure(
        [FromBody] ProcedureRequest request)

    // POST /api/v1/procedure/dataset  — 멀티 결과셋 (현행 ProcedureToDataSet 대체)
    [HttpPost("procedure/dataset")]
    public async Task<ActionResult<RuleResponse<DataSet>>> ExecuteProcedureDataSet(
        [FromBody] ProcedureRequest request)
}
```

#### 5.2.2 API 요청/응답 구조 (현행 MessageObject 호환)

```csharp
// 요청 (현행 MessageObject 구조 유지)
public class RuleRequest
{
    public RuleHead Head { get; set; }
    public Dictionary<string, object> Body { get; set; }
    public RuleTransaction Transaction { get; set; }
}

public class RuleHead
{
    public string RuleName { get; set; }
    public string UserId { get; set; }
    public string Uiid { get; set; }
    public string IpAddress { get; set; }
    public int Timeout { get; set; } = 30000;
}

// 응답 (현행 IResponse<T> 구조 유지)
public class RuleResponse<T>
{
    public bool Success { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public T Data { get; set; }
    public Dictionary<string, object> Parameters { get; set; }
}
```

#### 5.2.3 실시간 통신 (Kafka/WebSocket → SignalR)

> SignalR Hub는 용도별로 5개로 분리한다 (상세 토폴로지·메시지 우선순위는 **섹션 18.5** 참조).

```csharp
// ─────────────────────────────────────────────
// 5개 Hub 구조 (우선순위별 채널 분리)
// ─────────────────────────────────────────────

// [1] AlarmHub — 인터락·알람 최우선 처리 (/hubs/alarm)
[Authorize]
public class AlarmHub : Hub
{
    public async Task SubscribeEquipmentAlarm(string equipmentId)
    public async Task UnsubscribeEquipmentAlarm(string equipmentId)
    // 메시지: FdcInterlockTriggered, EquipmentAlarmRaised, EquipmentStateChanged(Alarm)
}

// [2] EquipmentHub — 설비 상태/Heartbeat (/hubs/equipment)
[Authorize]
public class EquipmentHub : Hub
{
    public async Task SubscribeEquipment(string equipmentId)
    public async Task UnsubscribeEquipment(string equipmentId)
    // 메시지: EquipmentStateChanged, ControlStateChanged, HeartbeatChanged
}

// [3] FdcHub — FDC 수집 데이터/Trend (/hubs/fdc)
[Authorize]
public class FdcHub : Hub
{
    public async Task SubscribeFdcParameter(string parameterId)
    // 메시지: FdcParameterCollected, FdcSummaryUpdated, SpecCheckCompleted
}

// [4] WorkHub — Lot/WorkOrder 진행 (/hubs/work)
[Authorize]
public class WorkHub : Hub
{
    public async Task SubscribePlant(string plantId)
    // 메시지: LotTrackedIn, LotTrackedOut, WorkOrderStarted, WorkOrderFinished
}

// [5] DeployHub — 클라이언트 DLL/Manifest 배포 알림 (/hubs/deploy)
[Authorize]
public class DeployHub : Hub
{
    // 메시지: ClientVersionChanged, DeploymentAvailable
}

// ─────────────────────────────────────────────
// Kafka → SignalR 브리지 (Hosted Service)
// ─────────────────────────────────────────────
public class KafkaToSignalRBridge : IHostedService
{
    // Kafka fdc.rawdata → FdcHub.FdcParameterCollected
    // Kafka equipment.state.changed → EquipmentHub + AlarmHub
    // Kafka equipment.alarm.fired → AlarmHub.EquipmentAlarmRaised
}
```

**Hub 라우트 등록 (Program.cs):**
```csharp
app.MapHub<AlarmHub>("/hubs/alarm");
app.MapHub<EquipmentHub>("/hubs/equipment");
app.MapHub<FdcHub>("/hubs/fdc");
app.MapHub<WorkHub>("/hubs/work");
app.MapHub<DeployHub>("/hubs/deploy");
```

### 5.3 SmartEES.Application - 비즈니스 서비스

#### 5.3.1 RuleExecutor (현행 Java Rule 클래스 대체)

> **v1.8 변경:** NexusFramework에 별도 RuleEngine이 없다. 비즈니스 룰은 C# Application Service로 구현하되,  
> `[WorkflowCallable]` 어트리뷰트를 붙여 NexusFramework의 `AssemblyInvocationNode`가 워크플로우에서 직접 호출할 수 있게 한다.  
> 기존 `IRule` / `RuleRegistry` 인터페이스는 유지하고, 서비스 메서드에 어트리뷰트를 추가하는 방식으로 확장한다.

현행 Java s-rule-* 번들의 각 비즈니스 룰을 C# 서비스 클래스로 1:1 전환.

```csharp
namespace SmartEES.Application.Rules
{
    // 기본 룰 인터페이스 (현행 Java Rule interface 대응)
    public interface IRule
    {
        string RuleName { get; }
        Task<RuleResult> ExecuteAsync(RuleContext context);
    }

    // 룰 실행 컨텍스트 (현행 Java ServiceObject 대응)
    public class RuleContext
    {
        public string UserId { get; set; }
        public string PlantId { get; set; }
        public string LanguageType { get; set; }
        public string DbmsType { get; set; }
        public Dictionary<string, object> Input { get; set; }
        public IDbConnection Connection { get; set; }
    }

    // 룰 레지스트리 (현행 OSGi BundleActivator 대응)
    public class RuleRegistry
    {
        private readonly Dictionary<string, IRule> _rules;
        
        public void Register(IRule rule)
        public IRule Get(string ruleName)
        public Task<RuleResult> ExecuteAsync(string ruleName, RuleContext context)
    }
}
```

#### NexusFramework WorkflowCallable 연계 — AssemblyInvocationNode 패턴

NexusFramework의 `AssemblyInvocationNode`는 외부 어셈블리의 메서드를 워크플로우 노드로 호출한다.  
`[WorkflowCallable]` 어트리뷰트로 마킹된 서비스 메서드는 워크플로우 JSON에서 `AssemblyInvocation` 타입 노드로 직접 참조된다.

```csharp
// SmartEES.Application.Rules/LotTrackInService.cs
// [WorkflowCallable] 어트리뷰트 → 워크플로우 파일에서 AssemblyInvocationNode로 호출 가능
public class LotTrackInService : IRule
{
    public string RuleName => "TrackInLot";

    [WorkflowCallable(DisplayName = "Lot TrackIn", Category = "EPT",
        Description = "Lot TrackIn 처리 — EPT_TB_CARRIER_LOT 삽입 + LOT 상태 변경")]
    public async Task<RuleResult> ExecuteAsync(RuleContext context)
    {
        // ... 비즈니스 로직 구현
        return RuleResult.Success();
    }
}

// SmartEES.Application.Rules/RecipeApprovalService.cs
public class RecipeApprovalService : IRule
{
    [WorkflowCallable(DisplayName = "Recipe Approve", Category = "RMS")]
    public async Task<RuleResult> ApproveAsync(
        string recipeId, string approverId, string approvalLevel,
        NodeContext nodeContext,          // NexusFramework 자동 주입
        CancellationToken cancellationToken)
    {
        // ... 권한 검증 + 상태 변경
    }
}
```

**워크플로우 JSON에서 AssemblyInvocationNode로 호출:**
```json
{
  "version": "1.0",
  "graph": {
    "nodes": [
      {
        "id": "trackIn",
        "type": "AssemblyInvocation",
        "properties": {
          "assemblyPath": "./Modules/Micube.SmartEES.Ept.dll",
          "typeName": "SmartEES.Application.Rules.LotTrackInService",
          "methodName": "ExecuteAsync",
          "arguments": { "lotId": "LOT-001", "equipmentId": "EQ-001" }
        }
      }
    ]
  }
}
```

**워크플로우 실행 시 메서드 자동 주입:**
- `NodeContext` 파라미터 → NexusFramework가 자동 주입
- `CancellationToken` 파라미터 → NexusFramework가 자동 주입
- `IServiceProvider` 파라미터 → NexusFramework가 자동 주입 (DI 컨테이너 접근)
- 기타 파라미터 → `arguments` JSON에서 매핑

#### 5.3.2 현행 Java 룰 → C# 서비스 매핑

| Java 룰 (s-rule-*) | C# 서비스 클래스 |
|---------------------|----------------|
| `TrackInLot.java` | `LotTrackInService` |
| `TrackOutLot.java` | `LotTrackOutService` |
| `MixingLotTrackInOut.java` | `MixingLotService` |
| `com_sp_selectEquipment` | `EquipmentQueryService` |
| `SaveEquipment` | `EquipmentCommandService` |

#### 5.3.3 ServiceObjectProcessor (현행 Java SO 패턴 대체)

```csharp
namespace SmartEES.Application
{
    // 현행 Java SO 패턴의 C# 구현 (v2.0: NexusCom ITransactionManager 기반 — 3.5와 동일 구성)
    public class ServiceObjectProcessor
    {
        private readonly IServiceObjectRepository _soRepository;
        private readonly ITransactionManager _txManager;
        private readonly INexaOneEESDbCapability _dialect;
        private readonly DatabaseEndpoint _endpoint;

        // Config/SO/Object.json, Attribute.json 기반 동적 CRUD
        public async Task<DataTable> SelectAsync(string objectId,
            Dictionary<string, object> conditions)
        public async Task<int> InsertAsync(string objectId,
            Dictionary<string, object> values)
        public async Task<int> UpdateAsync(string objectId,
            Dictionary<string, object> values,
            Dictionary<string, object> conditions)
        public async Task<int> DeleteAsync(string objectId,
            Dictionary<string, object> conditions)
    }
}
```

**ServiceObjectProcessor.InsertAsync 통합 구현 예시 (SqlTxnContext + 감사 포함):**

> **v2.0 변경:** 수동 `Commit/Rollback` 패턴을 폐기하고 NexusCom
> `ITransactionManager.ExecuteInTransactionAsync` 콜백으로 트랜잭션 범위를 래핑한다 (3.5와 동일).

```csharp
// SmartEES.Application/ServiceObjectProcessor.cs
public async Task<int> InsertAsync(string objectId, Dictionary<string, object> values)
{
    var objDef = await _soRepository.GetObjectDefinitionAsync(objectId);

    // 트랜잭션 범위는 ITransactionManager 콜백이 소유 — 커밋/롤백 자동 처리
    return await _txManager.ExecuteInTransactionAsync(_endpoint,
        async (conn, txn) =>
        {
            // 1. 트랜잭션 컨텍스트 생성 (SQL 방언 주입)
            var ctx = new SqlTxnContext(_dialect,
                UserInfo.Current.UserId, UserInfo.Current.PlantId);

            // 2. TXN_HIST_KEY 채번 (DBMS별 분기는 공급자가 처리)
            await ctx.GenerateTxnHistKeyAsync(conn, txn);

            // 3. 감사 필드 자동 주입
            InjectAuditFields(values, "INSERT");
            values["TXN_HIST_KEY"] = ctx.TxnHistKey;

            // 4. 메인 테이블 INSERT
            var sql = _sqlBuilder.BuildInsert(objDef.TableName, values);
            var affected = await conn.ExecuteAsync(sql, values, txn);

            // 5. _HIST 테이블 자동 복사 (historyEnabled=true인 오브젝트만)
            if (objDef.HistoryEnabled)
                await CopyToHistoryAsync(ctx, conn, txn, objDef.TableName, values, "INSERT");

            return affected;
        });
}
```

#### 5.3.4 SO 감사 메커니즘 (Audit Trail)

> **⚠️ 실제 소스 확인 사항:** SO 패턴의 Insert/Update/Delete 처리 시  
> **TXN_HIST_KEY 자동 생성**, **LAST_TXN_* 필드 자동 갱신**, **_HIST 테이블 행 자동 복사** 3가지  
> 감사 메커니즘이 동작한다. `ServiceObjectProcessor` 구현 시 반드시 포함해야 한다.

**[1] TXN_HIST_KEY — 트랜잭션 이력 키 자동 생성**

> **v2.0 변경:** `SqlTxnContext` 상세 설계는 **3.5로 일원화**한다 (NexusCom `ITransactionManager`
> 콜백 기반 + `INexaOneEESDbCapability` SQL 방언). 아래 코드도 개명·정합 반영.

```csharp
namespace SmartEES.Application
{
    /// <summary>
    /// INSERT/UPDATE/DELETE 실행 전 TXN_HIST_KEY를 채번하여
    /// 현재 트랜잭션 컨텍스트에 저장한다.
    /// TXN_HIST_KEY = YYYYMMDDHHMMSS + 6자리 순번 (DB 채번 함수 또는 Sequence)
    /// 트랜잭션 범위는 NexusCom ITransactionManager.ExecuteInTransactionAsync 콜백이 소유한다 (3.5 참조)
    /// </summary>
    public class SqlTxnContext
    {
        private readonly INexaOneEESDbCapability _dialect;

        // 현재 트랜잭션의 이력 키 (각 DML 전 채번)
        public string TxnHistKey { get; private set; } = string.Empty;
        public string UserId     { get; }
        public string PlantId    { get; }
        public DateTime TxnTime  { get; } = DateTime.UtcNow;

        public SqlTxnContext(INexaOneEESDbCapability dialect, string userId, string plantId)
        {
            _dialect = dialect;
            UserId   = userId;
            PlantId  = plantId;
        }

        // TXN_HIST_KEY 채번 — INexaOneEESDbCapability에 위임 (DBMS별 분기는 공급자가 처리)
        public async Task GenerateTxnHistKeyAsync(DbConnection conn, DbTransaction txn)
        {
            var sql = _dialect.GetSequenceSql("SEQ_TXN_HIST_KEY");
            TxnHistKey = (await conn.ExecuteScalarAsync<string>(sql, transaction: txn))!;
        }
    }
}
```

> **멀티DB 채번 전략 — 섹션 3.3/3.5 공급자 구현 참조:**
>
> | DBMS | `GetSequenceSql()` 반환값 | 비고 |
> |------|--------------------------|------|
> | MSSQL (기본) | `SELECT NEXT VALUE FOR [SEQ_TXN_HIST_KEY]` | Sequence 오브젝트 (구현 완료 — MsSqlProvider) |
> | PostgreSQL | `NEXTVAL('SEQ_TXN_HIST_KEY')::TEXT` | Sequence 함수 |
> | Oracle | `SEQ_TXN_HIST_KEY.NEXTVAL FROM DUAL` | 의사 컬럼 |
> | MySQL/MariaDB | `INSERT INTO SEQ_TXN_HIST_KEY_SEQ...` | 전용 채번 테이블 사용 |
>
> `SqlTxnContext`는 `INexaOneEESDbCapability` 인터페이스만 알고 있으며, 구체적인 DBMS는 DI 컨테이너가 주입한다.

**[2] LAST_TXN_* 필드 자동 갱신**

SO INSERT/UPDATE 시 다음 4개 감사 필드를 `ServiceObjectProcessor`가 **자동으로** 주입한다.  
개별 비즈니스 로직에서 수동으로 설정하지 않아도 된다.

| 필드명 | 채우는 값 | 적용 시점 |
|--------|-----------|-----------|
| `CREATOR` | `UserInfo.Current.UserId` | INSERT 시 |
| `CREATEDTIME` | `DateTime.UtcNow` | INSERT 시 |
| `MODIFIER` | `UserInfo.Current.UserId` | UPDATE/DELETE 시 |
| `MODIFIEDTIME` | `DateTime.UtcNow` | UPDATE/DELETE 시 |

```csharp
// ServiceObjectProcessor.InsertAsync 내부 — 자동 감사 필드 주입
private void InjectAuditFields(Dictionary<string, object> values, string operation)
{
    if (operation == "INSERT")
    {
        values["CREATOR"]     = UserInfo.Current.UserId;
        values["CREATEDTIME"] = DateTime.UtcNow;
        values["MODIFIER"]    = UserInfo.Current.UserId;
        values["MODIFIEDTIME"]= DateTime.UtcNow;
    }
    else if (operation is "UPDATE" or "DELETE")
    {
        values["MODIFIER"]    = UserInfo.Current.UserId;
        values["MODIFIEDTIME"]= DateTime.UtcNow;
    }
}
```

**[3] _HIST 테이블 자동 행 복사**

SO 오브젝트 정의(Config/SO/Object.json)에 `"historyEnabled": true`가 설정된 경우,  
INSERT/UPDATE/DELETE 후 원본 테이블의 해당 행을 `{TABLE}_HIST` 테이블로 자동 복사한다.

```csharp
// ServiceObjectProcessor.CopyToHistoryAsync 내부 (v2.0: conn/txn 명시 전달 — 3.5 호출과 동일 시그니처)
private async Task CopyToHistoryAsync(
    SqlTxnContext ctx, DbConnection conn, DbTransaction txn,
    string tableName, Dictionary<string, object> values, string operation = "INSERT")
{
    // 1. 원본 행 조회 (PK는 values에서 추출)
    var pkValues = ExtractPkValues(tableName, values);
    var row = await SelectByPkAsync(conn, txn, tableName, pkValues);

    // 2. _HIST 테이블에 행 삽입 (TXN_HIST_KEY + 감사 필드 추가)
    var histValues = new Dictionary<string, object>(row)
    {
        ["TXN_HIST_KEY"] = ctx.TxnHistKey,
        ["HIST_TYPE"]    = operation,        // "INSERT" / "UPDATE" / "DELETE" — 호출부가 전달
        ["HIST_TIME"]    = ctx.TxnTime,
        ["HIST_USER"]    = ctx.UserId,
    };

    await InsertRawAsync(conn, txn, $"{tableName}_HIST", histValues);
}
```

**[4] 전체 감사 흐름도:**

```
ServiceObjectProcessor.InsertAsync(objectId, values)
  │
  └─ ITransactionManager.ExecuteInTransactionAsync(endpoint, (conn, txn) => {
       ├─ SqlTxnContext.GenerateTxnHistKeyAsync(conn, txn)   ← TXN_HIST_KEY 채번
       ├─ InjectAuditFields(values, "INSERT")                ← CREATOR/CREATEDTIME 자동 주입
       ├─ INSERT INTO {TABLE} (...)                          ← 원본 테이블 삽입
       └─ CopyToHistoryAsync(ctx, conn, txn, tableName, values, "INSERT")
            └─ INSERT INTO {TABLE}_HIST (... + TXN_HIST_KEY, HIST_TYPE, HIST_TIME)
     })                                                      ← 커밋/롤백은 콜백이 자동 처리
```

**Object.json 감사 설정 예시:**
```json
// Config/SO/Object.json
{
  "objectId": "EQUIPMENT",
  "tableName": "STD_TB_EQUIPMENT",
  "historyEnabled": true,    // ← true 시 _HIST 자동 복사
  "attributes": [ ... ]
}
```

### 5.4 SmartEES.Infrastructure - 데이터 접근

#### 5.4.1 QueryRepository (현행 Config/Query/xml 로딩)

```csharp
namespace SmartEES.Infrastructure.Repositories
{
    public class QueryRepository : IQueryRepository
    {
        private readonly Dictionary<string, string> _queries;

        // Config/Query/xml/*.xml 파일 파싱 (현행 XML 쿼리 정의 구조 유지)
        public void LoadQueries(string queryDirectory)
        
        // ID + 버전 기반 SQL 검색 + Velocity 템플릿 처리
        public string GetQuery(string id, string version, string dbmsType,
            Dictionary<string, object> paramDict = null)
        
        // 파라미터 바인딩 (Dapper DynamicParameters)
        public DynamicParameters BuildParameters(
            Dictionary<string, object> paramDict)
    }
}
```

**Velocity 템플릿 처리 규칙:**

현행 XML 쿼리는 Apache Velocity 문법을 사용한다. C# 이전 시 다음 규칙으로 처리한다.

| 현행 Velocity 문법 | C# 처리 방식 |
|-------------------|-------------|
| `:변수명` | Dapper `@변수명` 파라미터로 치환 |
| `$변수명`, `${변수명}` | 런타임에 실제 값으로 문자열 치환 (SQL Injection 주의: whitelist 검증 필수) |
| `#if($조건변수)...#end` | 파라미터 존재 여부로 조건 블록 포함/제외 |
| `#foreach($item in $list)...#end` | IN 절 확장: `@p0,@p1,...` 형태로 치환 |
| `#set($var = value)` | 내부 변수 할당 — 치환 컨텍스트에서 처리 |

```csharp
// VelocityTemplateProcessor — Velocity → Dapper SQL 변환
public class VelocityTemplateProcessor
{
    // 1단계: #if/$조건 블록 처리 (파라미터 존재 여부 기준)
    // 2단계: #foreach → IN 파라미터 확장
    // 3단계: :변수 → @변수 치환
    // 4단계: $변수 → 화이트리스트 검증 후 문자열 치환
    public string Process(string velocitySql,
        Dictionary<string, object> parameters,
        out DynamicParameters dapperParams)
}

// 사용 예시
var sql = _processor.Process(rawSql, paramDict, out var dp);
var result = await conn.QueryAsync<DataRow>(sql, dp);
```

**현행 XML 쿼리 파일 구조 유지:**
```xml
<!-- Config/Query/xml/COM.xml -->
<Queries>
  <Query id="MICUBE.COMMON.SELECT.USER.PLANT.MAP.LIST" version="1.0" dbms="MSSQL">
    <![CDATA[
      SELECT p.PLANTID, p.PLANTNAME
      FROM STD_TB_PLANT p
      JOIN COM_TB_USER_PLANT_MAP m ON p.PLANTID = m.PLANTID
      WHERE m.USERID = :USERID
      #if($LANGUAGETYPE)
        AND p.LANGUAGETYPE = :LANGUAGETYPE
      #end
    ]]>
  </Query>
</Queries>
```

#### 5.4.2 DapperDbContext (멀티DB 지원)

```csharp
namespace SmartEES.Infrastructure
{
    // 현행 Config/Datasource/mssql-datasource.json 등 지원
    public class DapperDbContext
    {
        // 데이터소스 ID로 연결 팩토리 (현행 SO의 datasource 필드 대응)
        public IDbConnection GetConnection(string datasourceId = "default")
        
        // 트랜잭션 래퍼
        public async Task<T> ExecuteInTransactionAsync<T>(
            Func<IDbConnection, IDbTransaction, Task<T>> action,
            string datasourceId = "default")
    }
    
    // 지원 DB 목록 (현행과 동일)
    public enum DbmsType { MSSQL, PostgreSQL, MySQL, MariaDB, Oracle, SQLite }
}
```

---

## 6. UI 레이어 상세 설계 (C# Blazor 웹)

현행 C# WinForms를 **ASP.NET Core Blazor Server** 기반 웹 애플리케이션으로 마이그레이션합니다.  
WinForms 폼 클래스는 Blazor 컴포넌트(`.razor`)로 1:1 대응 전환하며, DevExpress WinForms 컨트롤은 DevExpress Blazor 컴포넌트로 교체합니다.

### 6.1 아키텍처 개요

```
┌──────────────────────────────────────────────────────────────────┐
│                  NexaOne.Web  (Blazor Server)                 │
│                       브라우저 (Chrome / Edge)                     │
│                                                                    │
│   ┌─────────────────────────────────────────────────────────────┐ │
│   │                  Blazor 컴포넌트 트리                          │ │
│   │   MainLayout ─ NavMenu ─ 업무 페이지 (.razor)                 │ │
│   │   SmartConditionGrid / SmartGrid / SmartPopup 공통 컴포넌트   │ │
│   └──────────────────────────────┬──────────────────────────────┘ │
└─────────────────────────────────┼──────────────────────────────────┘
                SignalR (WebSocket) │ + 직접 서비스 주입 (DI)
┌─────────────────────────────────▼──────────────────────────────────┐
│                    NexaOne.API / NexaOne.Server                 │
│   ASP.NET Core Web API  +  Spring.NET 도메인 서비스                  │
│   Application Service (EquipmentService, UserService 등)            │
└─────────────────────────────────────────────────────────────────────┘
```

**Blazor Server** 선택 이유:
- 인트라넷 MES 환경 — 낮은 레이턴시, 서버 자원 집중 관리
- C# 코드 공유 — 도메인 모델·서비스를 UI에서 직접 참조
- WinForms 마이그레이션에 가장 자연스러운 전환 경로

### 6.2 솔루션 추가 프로젝트

```
src/
├── 01.Web/
│   ├── NexaOne.Web/                    ← Blazor Server 앱 (새 프로젝트)
│   │   ├── Pages/                          ← 업무 페이지 (.razor)
│   │   │   ├── Auth/Login.razor
│   │   │   ├── Mdm/Equipment.razor
│   │   │   ├── Ept/EquipmentAlarm.razor
│   │   │   ├── Fdc/FdcInterlockRule.razor
│   │   │   ├── Rms/Recipe.razor
│   │   │   ├── Qms/Defect.razor
│   │   │   ├── Ems/WorkOrder.razor
│   │   │   ├── Ppm/ProductionPlan.razor
│   │   │   ├── Dlv/DeliveryOrder.razor
│   │   │   └── Sys/User.razor
│   │   ├── Shared/                         ← 공통 레이아웃·컴포넌트
│   │   │   ├── MainLayout.razor
│   │   │   ├── NavMenu.razor
│   │   │   └── SmartConditionGrid.razor    ← SmartConditionBaseForm 대응
│   │   ├── Components/                     ← 재사용 UI 컴포넌트
│   │   │   ├── SmartGrid.razor
│   │   │   ├── SmartPopup.razor
│   │   │   └── ConditionPanel.razor
│   │   ├── Program.cs
│   │   └── NexaOne.Web.csproj
```

### 6.3 기술 스택 매핑

| 현행 WinForms | 타겟 Blazor |
|--------------|-------------|
| `XtraForm` (DevExpress) | `ComponentBase` (Blazor) |
| `SmartConditionBaseForm` | `SmartConditionGrid.razor` 컴포넌트 |
| `SmartBandedGrid` (DevExpress GridControl) | DevExpress Blazor `DxGrid` |
| `SmartPopupBaseForm` | `DxPopup` 오버레이 컴포넌트 |
| `ConditionCollection` (13종 컨트롤) | `DxComboBox`, `DxTextBox`, `DxDateRangePicker` 등 |
| `MainForm` (MDI) | `MainLayout.razor` + 탭 라우팅 |
| `LoginForm` | `Login.razor` |
| Ninject DI | ASP.NET Core 내장 DI |
| `Application.Run()` | `WebApplication.Run()` |
| `DevExpress XtraBars` (메뉴) | `DxMenu` / NavMenu 컴포넌트 |
| `SmartUserControl` | 재사용 `.razor` 컴포넌트 |

### 6.4 Program.cs (Blazor Server 부트스트랩)

```csharp
// NexaOne.Web/Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 도메인 서비스 등록 (모듈별 서비스 직접 주입)
builder.Services.AddNexaOneEESServices(builder.Configuration);
builder.Services.AddScoped<EquipmentService>();
builder.Services.AddScoped<UserService>();
// ... 나머지 9개 모듈 서비스

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
```

### 6.5 MainLayout.razor (MainForm 대응)

```razor
@inherits LayoutComponentBase

<div class="main-layout">
    <header class="app-header">
        <span class="logo">NexaOne SmartEES</span>
        <span class="user-info">@UserInfo.Current.UserId</span>
        <select @onchange="OnLanguageChange">
            <option value="ko-KR">한국어</option>
            <option value="en-US">English</option>
        </select>
        <button @onclick="Logout">로그아웃</button>
    </header>

    <NavMenu />

    <div class="tab-container">
        @Body
    </div>

    <footer class="status-bar">
        @UserInfo.Current.PlantId | 서버 연결됨
    </footer>
</div>
```

### 6.6 SmartConditionGrid.razor (SmartConditionBaseForm 대응)

현행 `SmartConditionBaseForm`을 제네릭 Blazor 컴포넌트로 추상화합니다.

```razor
@typeparam TRow
@code {
    [Parameter] public RenderFragment ConditionPanel { get; set; } = default!;
    [Parameter] public List<GridColumn> Columns { get; set; } = new();
    [Parameter] public Func<Task<IEnumerable<TRow>>> OnSearch { get; set; } = default!;
    [Parameter] public Func<IEnumerable<TRow>, Task> OnSave { get; set; } = default!;
    [Parameter] public EventCallback<TRow> OnDelete { get; set; }

    private IEnumerable<TRow> _rows = [];
    private bool _loading;

    private async Task Search() {
        _loading = true;
        _rows = await OnSearch();
        _loading = false;
    }
}

<div class="condition-panel">
    @ConditionPanel
    <DxButton Text="검색" Click="Search" />
</div>

<DxToolbar>
    <DxToolbarItem Text="저장" Click="SaveAll" />
    <DxToolbarItem Text="삭제" Click="DeleteSelected" />
    <DxToolbarItem Text="Excel" Click="ExportExcel" />
</DxToolbar>

<DxGrid Data="_rows" @ref="_grid" EditMode="GridEditMode.EditRow">
    @foreach (var col in Columns) {
        <DxGridDataColumn FieldName="@col.Field" Caption="@col.Label" Width="@col.Width" />
    }
</DxGrid>
```

### 6.7 업무 페이지 예시 — Equipment.razor

```razor
@page "/mdm/equipment"
@inject EquipmentService EquipmentService

<SmartConditionGrid TRow="EquipmentRow"
    OnSearch="SearchAsync"
    OnSave="SaveAsync"
    Columns="_columns">

    <ConditionPanel>
        <DxComboBox Data="_plants" @bind-Value="_plantId" />
        <DxTextBox @bind-Text="_equipmentId" Placeholder="설비ID" />
        <DxTextBox @bind-Text="_equipmentName" Placeholder="설비명" />
    </ConditionPanel>

</SmartConditionGrid>

@code {
    private string? _plantId, _equipmentId, _equipmentName;
    private IEnumerable<PlantDto> _plants = [];

    private readonly List<GridColumn> _columns = [
        new("EQUIPMENTID",   "설비ID",   150),
        new("EQUIPMENTNAME", "설비명",   200),
        new("PLANTID",       "플랜트",   150),
        new("EQUIPMENTTYPE", "설비유형", 150),
    ];

    private async Task<IEnumerable<EquipmentRow>> SearchAsync() {
        var result = await EquipmentService.GetByPlantAsync(_plantId);
        return result.Select(EquipmentRow.From);
    }

    private async Task SaveAsync(IEnumerable<EquipmentRow> rows) {
        foreach (var row in rows.Where(r => r.State == "added"))
            await EquipmentService.AddAsync(row.ToDomain());
        foreach (var row in rows.Where(r => r.State == "modified"))
            await EquipmentService.UpdateAsync(row.ToDomain());
    }
}
```

### 6.8 로그인 화면 — Login.razor

```razor
@page "/login"
@inject IJwtService JwtService
@inject NavigationManager Nav

<div class="login-container">
    <h2>NexaOne SmartEES</h2>
    <DxTextBox @bind-Text="_userId" Placeholder="사용자 ID" />
    <DxTextBox @bind-Text="_password" Password="true" Placeholder="비밀번호" />
    <DxButton Text="로그인" Click="LoginAsync" />
    @if (_error != null) { <span class="error">@_error</span> }
</div>

@code {
    private string _userId = "", _password = "", _error = "";

    private async Task LoginAsync() {
        var result = await JwtService.ValidateAsync(_userId, _password);
        if (result.IsSuccess)
            Nav.NavigateTo("/");
        else
            _error = result.Error.Description;
    }
}
```

### 6.9 폼 클래스 → Blazor 컴포넌트 매핑

| 현행 WinForms 클래스 | Blazor 컴포넌트 / 페이지 | 위치 |
|----------------------|--------------------------|------|
| `SmartConditionBaseForm` | `SmartConditionGrid<TRow>.razor` | `Shared/` |
| `SmartConditionForm` | `SmartConditionOnly.razor` | `Shared/` |
| `HistoryForm` | `SmartHistoryGrid<TRow>.razor` | `Shared/` |
| `SmartPopupCheckGridForm` | `SmartPopupSingle<TRow>.razor` | `Components/` |
| `SmartPopupMultiGridForm` | `SmartPopupMulti<TRow>.razor` | `Components/` |
| `SmartUserControl` | 재사용 `.razor` 컴포넌트 | `Components/` |
| `MainForm` | `MainLayout.razor` | `Shared/` |
| `LoginForm` | `Login.razor` | `Pages/Auth/` |
| 각 업무 폼 (9개 도메인) | 도메인별 `.razor` 페이지 | `Pages/{Domain}/` |

### 6.10 다국어 지원

```csharp
// ILanguageService.cs
public interface ILanguageService
{
    string Current { get; }
    string Get(string key);
    Task SetLanguageAsync(string langCode);
}
```

`SYS_TB_MULTI_LANGUAGE` 테이블에서 로드하여 `IMemoryCache`로 캐싱합니다.  
언어 전환 시 `StateHasChanged()` 트리거로 전체 컴포넌트 재렌더링합니다.

| 언어 | 코드 |
|------|------|
| 한국어 | `ko-KR` |
| 영어 | `en-US` |
| 중국어 (간체) | `zh-CN` |
| 베트남어 | `vi-VN` |
| 라오어 | `lo-LO` |
---

## 10. 도메인 모듈별 상세 설계

> **v1.9 전환 안내:** 이 섹션의 C# WinForms 코드 블록은 **현행 참조용**입니다.  
> 타겟 구현에서는 `SmartConditionBaseForm` 상속 코드 대신 **섹션 6.3의 JSON 메타데이터** 방식으로 화면을 정의합니다.  
> 각 도메인 화면의 조건·컬럼·레이아웃 구성은 SmartUX 웹 디자이너에서 관리하고, 백엔드 서비스는 `NexaOne.{Module}` C# 도메인 모듈에서 구현합니다.

### 10.1 Micube.SmartEES.Mdm (마스터 데이터 관리)

#### 10.1.1 Equipment (설비 마스터)

```csharp
public class Equipment : SmartConditionBaseForm
{
    protected override void InitializeContent()
    {
        // 검색 조건 (현행 유지)
        Conditions.Add("PLANTID").AsComboBox(query: "GetPlantList").WithLabel("Plant");
        Conditions.Add("EQUIPMENTID").AsTextBox().WithLabel("설비ID");
        Conditions.Add("EQUIPMENTNAME").AsTextBox().WithLabel("설비명");

        // 그리드 컬럼 (현행 완전 유지)
        Grid.AddTextBoxColumn("EQUIPMENTID")
            .AsKey().AsRequired().WithLabel("설비ID").WithWidth(150);
        Grid.AddLanguageColumn("EQUIPMENTNAME")
            .AsRequired().WithLabel("설비명").WithWidth(200);
        Grid.AddTextBoxColumn("DESCRIPTION")
            .WithLabel("설명").WithWidth(200);
        Grid.AddComboBoxColumn("PLANTID")
            .WithQuery("GetPlantList")
            .WithDefaultValue(() => UserInfo.Current.PlantId)
            .WithLabel("플랜트").WithWidth(150);
        Grid.AddPopupColumn("AREAID")
            .WithPopupForm("AreaPopup")
            .WithLabel("구역").WithWidth(120);
        Grid.AddComboBoxColumn("EQUIPMENTTYPE")
            .WithQuery("GetEquipmentTypeList")
            .WithLabel("설비유형").WithWidth(150);
        Grid.AddPopupColumn("PARENTEQUIPMENTID")
            .WithPopupForm("EquipmentPopup")
            .WithLabel("상위설비").WithWidth(150);
        Grid.AddTextBoxColumn("VENDOR").WithLabel("제조사").WithWidth(120);
        Grid.AddTextBoxColumn("MODEL").WithLabel("모델").WithWidth(120);
        Grid.AddComboBoxColumn("VALIDSTATE")
            .WithItems(new[] { "Valid", "Invalid" })
            .WithDefaultValue("Valid")
            .WithLabel("유효상태").WithWidth(100);
        // 감사 필드
        Grid.AddTextBoxColumn("CREATOR").AsAudit().WithLabel("생성자");
        Grid.AddDateColumn("CREATEDTIME").AsAudit().WithLabel("생성일시");
        Grid.AddTextBoxColumn("MODIFIER").AsAudit().WithLabel("수정자");
        Grid.AddDateColumn("MODIFIEDTIME").AsAudit().WithLabel("수정일시");
    }

    protected override async Task OnSearchAsync()
    {
        var param = Conditions.GetValues();
        var result = await SqlExecuter.ProcedureAsync(
            "com_sp_selectEquipment", param);
        Grid.SetDataSource(result);
    }

    protected override async Task OnToolbarSaveClick()
    {
        if (!OnValidateContent()) return;
        var changedRows = Grid.GetChangedRows();
        await SqlExecuter.ProcedureAsync("SaveEquipment",
            new Dictionary<string, object>
            {
                ["ROWS"] = changedRows,
                ["USERID"] = UserInfo.Current.UserId
            });
        await OnSearchAsync();
    }

    protected override bool OnValidateContent()
        => Grid.CheckValidation();
}
```

#### 10.1.2 MDM 모듈 화면 목록

| 클래스 | 화면명 | 저장 프로시저 |
|--------|--------|-------------|
| `Equipment` | 설비 마스터 | `com_sp_selectEquipment`, `SaveEquipment` |
| `EquipmentClass` | 설비 분류 | `com_sp_selectEquipmentClass` |
| `EquipmentState` | 설비 상태 | `com_sp_selectEquipmentState` |
| `Area` | 구역 관리 | `com_sp_selectArea` |
| `Plant` | 플랜트 관리 | `com_sp_selectPlant` |
| `Product` | 제품 마스터 | `com_sp_selectProduct` |
| `ProcessSegment` | 공정 세그먼트 | `com_sp_selectProcessSegment` |
| `Code` | 코드 관리 | `com_sp_selectCode` |
| `CodeClass` | 코드 분류 | `com_sp_selectCodeClass` |

### 10.2 Micube.SmartEES.Ept (설비 성능 추적)

#### 10.2.1 EquipmentAlarmHistory (설비 알람 이력)

```csharp
public class EquipmentAlarmHistory : SmartConditionBaseForm
{
    // DevExpress Chart / PivotGrid 컴포넌트 (현행 유지)
    private ChartControl _columnChart;
    private ChartControl _pieChart;
    private ChartControl _lineChart;
    private PivotGridControl _pivotGrid;

    protected override void InitializeContent()
    {
        // 기간 조건
        Conditions.Add("PERIODFR").AsDateRange("PERIODFR", "PERIODTO")
            .WithLabel("조회 기간");
        // 설비 선택
        Conditions.Add("EQUIPMENTID")
            .AsMultiSelect(query: "GetEquipmentList")
            .WithLabel("설비");
        // 분석 기준
        Conditions.Add("WORSTTYPE")
            .AsComboBox(items: new[] { "AlarmCount", "ElapsedTime" })
            .WithLabel("분석 기준");
    }

    protected override async Task OnSearchAsync()
    {
        // 유효성 검증 (날짜 범위, 설비 필수)
        if (!ValidateDateRange()) return;
        if (!ValidateEquipmentSelection()) return;

        var param = Conditions.GetValues();
        
        // 분기 처리 (현행 로직 완전 유지)
        if (IsEquipmentCriteria(param))
        {
            if (IsWorstTypeAlarmCount(param))
                await LoadAlarmCountData(param);
            else
                await LoadElapsedTimeData(param);
        }

        InitializeColumnChart();
        InitializePieChart();
        InitializeLineChart();
        InitializePivotGrid();
    }

    // 피벗 드릴다운 (현행 로직 유지)
    private void TabPivot_CellDoubleClick(object sender, PivotCellEventArgs e)
    {
        // 피벗 셀의 행/열 값 조합으로 필터 파라미터 구성
        // EquipmentAlarmHistoryPopup 열기
        var popupParams = BuildDrilldownParameters(e);
        OpenMenu("EquipmentAlarmHistoryPopup", popupParams);
    }
}
```

#### 10.2.2 EPT 모듈 화면 목록

| 클래스 | 화면명 |
|--------|--------|
| `EquipmentAlarmHistory` | 설비 알람 이력 |
| `EquipmentAvailabilityHistory` | 설비 가동률 이력 |
| `EquipmentStateMonitoring` | 설비 상태 모니터링 |
| `EquipmentStateChange` | 설비 상태 변경 |
| `EquipmentLossAnalysis` | 설비 손실 분석 |
| `EquipmentLossManualInput` | 손실 수동 입력 |
| `Index` | KPI 지수 관리 |
| `InterestedIndex` | 관심 지수 |
| `MachineCycleChart` | 머신 사이클 차트 |
| `OverallEquipmentEffectiveness` | OEE 종합효율 |
| `Layout` | 레이아웃 관리 |
| `LayoutEdit` | 레이아웃 편집 |

### 10.3 Micube.SmartEES.SystemManagement (시스템 관리)

| 화면 | 관련 테이블 | 설명 |
|------|-------------|------|
| 사용자 관리 | SYS_TB_USER | 사용자 등록/수정/삭제 |
| 권한 관리 | SYS_TB_AUTHORITY | 역할 기반 접근제어 |
| 메뉴 관리 | SYS_TB_MENU | 메뉴 계층 구조 관리 |
| 코드 관리 | SYS_TB_CODE | 시스템 공통 코드 |
| 사전 관리 | SYS_TB_DICTIONARY | 다국어 텍스트 |
| 로그 조회 | SYS_TB_LOG | 감사 로그 조회 |
| 데이터소스 관리 | SYS_TB_DATASOURCE | DB 연결 설정 |
| UI 관리 | SYS_TB_UI, UX_PROJECT | 웹 UI 프로젝트 파일 |

### 10.4 Micube.SmartEES.Fdc (설비데이터 수집)

> **v1.8 변경:** `FdcCollectorService`가 NexusFramework `PlantController` + `Machine` + `IDeviceInterface` 패턴을 사용하여 설비 연결 및 데이터 수집을 관리한다.  
> 개별 설비는 `Machine`으로 등록하고, `PlantController`가 전체 설비 라이프사이클(초기화/시작/정지/긴급정지)을 조율한다.

#### 10.4.1 화면 목록

| 화면 | 기본 클래스 | 관련 테이블 | 설명 |
|------|-------------|-------------|------|
| FDC 파라미터 관리 | `SmartConditionBaseForm` | `FDC_TB_PARAMETER` | 수집 파라미터 마스터 관리 |
| FDC 파라미터 그룹 | `SmartConditionBaseForm` | `FDC_TB_PARAMETER_GROUP` | 파라미터 그룹화 |
| FDC 수집 데이터 조회 | `SmartConditionBaseForm` | `FDC_TB_COLLECT_DATA` | 수집 데이터 조회/차트 |
| FDC 인터락 규칙 | `SmartConditionBaseForm` | `FDC_TB_INTERLOCK_RULE` | 설비 자동 정지 규칙 설정 |
| FDC 인터락 이력 | `HistoryForm` | `FDC_TB_INTERLOCK_HISTORY` | 인터락 발생/해제 이력 |
| FDC 알람 설정 | `SmartConditionBaseForm` | `FDC_TB_ALARM_CONFIG` | 임계치 기반 알람 설정 |
| FDC 알람 이력 | `HistoryForm` | `FDC_TB_ALARM_HISTORY` | FDC 알람 발생 이력 |

#### 10.4.2 실시간 수집 아키텍처 — NexusFramework PlantController 기반

```
PlantController (NexusFramework)
  ├─ Machine["EQ-001"] (OpcUaMachine)    ← OpcUaDeviceInterface(IDeviceInterface) → NexusLogic.Plc.OpcUa
  ├─ Machine["EQ-002"] (OpcUaMachine)    ← OpcUaDeviceInterface(IDeviceInterface) → NexusLogic.Plc.OpcUa
  └─ HostStateMachine (IStateMachine<HostState, HostCommand>)
       └─ InitializeAsync → StartAsync → StopAsync / AbortAsync

각 Machine.StartAsync() 내 수집 루프:
  IDeviceInterface.Subscribe(nodeIds, onDataReceived)
    └─ EquipmentDataPoint 수신 → FdcCollectorService.OnDataReceived()
         └─ Kafka Producer: fdc.rawdata 토픽 발행

FdcConsumerService (Kafka Consumer) ← fdc.rawdata 구독
  ├─ 파라미터 유효성 검사
  ├─ FDC_TB_COLLECT_DATA 저장
  ├─ 인터락 규칙 평가 → FDC_TB_INTERLOCK_HISTORY
  └─ SignalR FdcHub → 클라이언트 실시간 차트 갱신
```

> **v2.0 기본 구현:** 위는 Kafka 경유 분산 수집의 전체 그림이다. 기본 구현은 `FdcCollectorService`가
> 구독 이벤트를 **직접** `FDC_TB_COLLECT_DATA`에 적재하고 인터락을 평가한다(Kafka 미경유, 10.4.3 적응 단락 참조).
> Kafka 경유는 수집 노드 분산이 필요해질 때 `fdc.rawdata` 발행을 추가해 활성화한다.

#### 10.4.3 NexusFramework Machine + PlantController 구현

> **구현은 본 절을 다음과 같이 적응한다 (v2.0).** 아래 `IEquipmentDriver` 청사진 대신,
> NexusFramework `Machine`에 **`OpcUaDeviceInterface`(IDeviceInterface, 3.6.3)**를
> `Machine.AddInterface()`로 장착한다(Machine이 lifecycle을 인터페이스에 위임). 수집 데이터 흐름은
> **`FdcCollectorService`**(NexaOne.FDC.Application)가 담당한다 — 연결의 `IPlcSubscriptionProvider`로
> 태그를 구독하고, `PlcTagChangeEvent`를 받아 값/품질을 변환한 뒤
> `FdcDataService.RecordDataAsync`로 `FDC_TB_COLLECT_DATA`에 적재하고,
> `FdcInterlockService.EvaluateAsync`로 인터락을 평가한다. 발동 시
> `FdcInterlockService.RecordTriggerAsync`로 **`FDC_INTERLOCK_HISTORY`에 직접 적재**하고
> `InterlockTriggered` 이벤트를 발생시킨다 — 설비 정지·SignalR 알림은 이 이벤트의 호스트 구독자가 처리한다.
> Kafka 경유(`fdc.rawdata`)는 대량·분산 수집이 필요할 때의 선택지로 두고, 기본 구현은 직접 적재한다.
> 설비-엔드포인트 매핑은 **`FDC_EQUIPMENT_ENDPOINT`(FdcEquipmentEndpoint 도메인/리포지토리)**로 구현했다
> — 통신 관심사를 MDM 마스터와 분리해 FDC가 소유하며, 한 설비에 복수 엔드포인트를 허용한다.
> 이 매핑을 NexusLogic `PlcEndpoint`로 변환해 Machine을 구동하는 `PlantController` 오케스트레이션(HostedService)은 다음 단계다.
> 수집 변환·인터락 연결은 `FdcCollectorServiceTests`(11개)·`OpcUaDeviceInterfaceTests`(8개)가 검증한다.

```csharp
// NexaOne.FDC/Application/Fdc/FdcCollectorService.cs (v2.0 구현 완료 — 핵심 글루)
public sealed class FdcCollectorService
{
    private readonly FdcDataService _dataService;
    private readonly FdcInterlockService? _interlockService;
    public event EventHandler<FdcInterlockTriggeredEventArgs>? InterlockTriggered;

    // 이미 Start된 디바이스의 연결에 구독을 걸어 데이터 흐름을 연결
    public async Task StartCollectingAsync(OpcUaDeviceInterface device,
        IEnumerable<PlcSubscription> subscriptions, CancellationToken ct = default)
    {
        var conn = device.Connection ?? throw new InvalidOperationException("...");
        await conn.SubscriptionProvider.StartAsync(conn.Endpoint, subscriptions,
            evt => OnTagChangeAsync(device.InterfaceName, evt, ct), ct);
    }

    public async Task OnTagChangeAsync(string equipmentId, PlcTagChangeEvent evt, CancellationToken ct = default)
    {
        var value = ToDecimal(evt.After);
        var recorded = await _dataService.RecordDataAsync(
            Guid.NewGuid().ToString("N"), equipmentId, evt.TagName, value, MapQuality(evt.Quality), ct);
        if (recorded.IsFailure) return;                              // 미정의 파라미터 등 — 폭주 방지
        if (_interlockService is not null)
        {
            var interlock = await _interlockService.EvaluateAsync(equipmentId, evt.TagName, value, ct);
            if (interlock.IsTriggered)
            {
                await _interlockService.RecordTriggerAsync(equipmentId, evt.TagName, value, interlock, ct);  // FDC_INTERLOCK_HISTORY
                InterlockTriggered?.Invoke(this,
                    new FdcInterlockTriggeredEventArgs(equipmentId, evt.TagName, value, interlock));
            }
        }
    }
    // MapQuality(PlcQuality → "Good"/"Bad"/"Uncertain"), ToDecimal(object? → decimal) — 상세는 실제 파일
}
```

<details><summary>설계 청사진 (IEquipmentDriver 기반 — 위 적응 단락으로 대체됨)</summary>

```csharp
// Micube.SmartEES.Fdc/Services/FdcMachine.cs
// NexusFramework Machine을 상속하여 설비별 FDC 수집 로직 구현
public class FdcMachine : Machine
{
    private readonly IEquipmentDriver _driver;       // OpcUaDeviceInterface (NexusLogic.Plc.OpcUa 래핑 — 3.6.3)
    private readonly IMessageBrokerDriver _kafka;    // 어댑터 글루 (NexusCom KafkaDriver 래핑 — 3.6.1)
    private readonly FdcParameterConfig _config;

    public FdcMachine(string name, IEquipmentDriver driver,
        IMessageBrokerDriver kafka, FdcParameterConfig config)
        : base(name)
    {
        _driver = driver;
        _kafka  = kafka;
        _config = config;
    }

    // NexusFramework Machine 오버라이드
    protected override async Task OnStartAsync(CancellationToken ct)
    {
        await _driver.ConnectAsync(_config.Endpoint, ct);

        // IDeviceInterface 구독 시작
        await _driver.SubscribeAsync(
            _config.NodeIds,
            dataPoint => _ = OnDataReceivedAsync(dataPoint),
            _config.SamplingIntervalMs);
    }

    protected override async Task OnStopAsync(CancellationToken ct)
    {
        await _driver.DisconnectAsync();
    }

    private async Task OnDataReceivedAsync(EquipmentDataPoint dp)
    {
        var payload = JsonSerializer.Serialize(new
        {
            EquipmentId = Name,
            ParameterId = dp.NodeId,
            Value       = dp.Value,
            Timestamp   = dp.Timestamp,
            Quality     = dp.Quality
        });
        await _kafka.PublishAsync("fdc.rawdata", Name, payload);
    }
}

// Micube.SmartEES.Fdc/Services/FdcCollectorHostedService.cs
// ASP.NET Core IHostedService — PlantController 생명주기 관리
public class FdcCollectorHostedService : IHostedService
{
    private readonly PlantController _plantController;

    public FdcCollectorHostedService(
        IConfiguration config, IEquipmentDriverFactory driverFactory,
        IMessageBrokerDriver kafka, IFdcParameterRepository repo)
    {
        _plantController = new PlantController();

        // DB에서 활성 설비 목록 로드 → Machine 등록
        var equipments = repo.GetActiveFdcEquipmentsAsync().GetAwaiter().GetResult();
        foreach (var eq in equipments)
        {
            var driver = driverFactory.Create(eq.ProtocolType, eq.DriverConfig);
            var machine = new FdcMachine(eq.EquipmentId, driver, kafka, eq.ParameterConfig);
            _plantController.RegisterMachine(machine);
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _plantController.InitializeAsync(ct);
        await _plantController.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _plantController.StopAsync(ct);
        await _plantController.DisposeAsync();
    }
}
```

</details>

#### 10.4.4 PlantController 상태 머신 — 설비 제어

```csharp
// PlantController는 NexusFramework HostStateMachine을 통해 전체 설비 상태를 관리
// SmartEES API에서 설비 일괄 제어
[HttpPost("fdc/equipment/start")]
public async Task<IActionResult> StartAll()
{
    await _plantController.StartAsync();
    return Ok(new { state = _plantController.StateMachine?.Current });
}

[HttpPost("fdc/equipment/stop")]
public async Task<IActionResult> StopAll()
{
    await _plantController.StopAsync();
    return Ok();
}

[HttpPost("fdc/equipment/abort")]
public async Task<IActionResult> AbortAll([FromBody] string reason)
{
    await _plantController.AbortAsync(reason);
    return Ok(new { state = _plantController.StateMachine?.Current });
}

// 특정 설비만 개별 제어
[HttpPost("fdc/equipment/{equipmentId}/manual")]
public async Task<IActionResult> EnterManual(string equipmentId)
{
    await _plantController.EnterManualAsync();
    return Ok(new { operationMode = _plantController.OperationMode });
}
```

#### 10.4.5 인터락 규칙 엔진

```csharp
// FdcConsumerService에서 Kafka 메시지 수신 후 인터락 평가
public class FdcConsumerService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _kafka.SubscribeAsync("fdc.rawdata", "fdc-consumer",
            async (key, payload) =>
            {
                var dp = JsonSerializer.Deserialize<FdcDataPoint>(payload)!;

                // DB 저장
                await _collectRepo.SaveAsync(dp);

                // 인터락 규칙 평가
                var result = await _interlockService.EvaluateAsync(
                    dp.EquipmentId, dp.ParameterId, dp.Value);

                if (result.IsTriggered)
                {
                    if (result.Action == "STOP")
                        await _plantController.AbortAsync($"인터락: {dp.ParameterId} = {dp.Value}");
                    await _alarmHub.SendAlarmAsync(dp.EquipmentId, result.Message);
                }

                // SignalR 실시간 푸시
                await _fdcHub.BroadcastDataPointAsync(dp);
            }, ct);
    }
}

public class FdcInterlockService : IFdcInterlockService
{
    public async Task<InterlockResult> EvaluateAsync(
        string equipmentId, string parameterId, decimal value)
    {
        var rules = await _ruleRepo.GetActiveRulesAsync(equipmentId, parameterId);
        foreach (var rule in rules)
        {
            if (rule.Evaluate(value))
            {
                await RecordInterlockHistoryAsync(rule, value);
                return InterlockResult.Triggered(rule.Action);  // "STOP" | "ALARM" | "NOTIFY"
            }
        }
        return InterlockResult.Pass();
    }
}
```

### 10.5 Micube.SmartEES.Rms (레시피 관리)

#### 10.5.1 화면 목록

| 화면 | 기본 클래스 | 관련 테이블 | 설명 |
|------|-------------|-------------|------|
| 레시피 마스터 | `SmartConditionBaseForm` | `RMS_TB_RECIPE` | 레시피 기본 정보 관리 |
| 레시피 파라미터 | `SmartConditionBaseForm` | `RMS_TB_RECIPE_PARAM` | 레시피별 파라미터 설정값 |
| 레시피 버전 이력 | `HistoryForm` | `RMS_TB_RECIPE_HIST` | 레시피 변경 이력 (SO audit) |
| 레시피 승인 관리 | `SmartConditionBaseForm` | `RMS_TB_RECIPE_APPROVAL` | 승인 워크플로우 진행 현황 |
| 설비-레시피 매핑 | `SmartConditionBaseForm` | `RMS_TB_EQUIPMENT_RECIPE` | 설비별 적용 가능 레시피 |
| 레시피 다운로드 이력 | `HistoryForm` | `RMS_TB_DOWNLOAD_HISTORY` | 설비로 전송된 이력 |

#### 10.5.2 레시피 승인 워크플로우

```
레시피 저장(Draft) → 승인요청(Pending) → 1차승인(Approved1) → 최종승인(Approved) → 설비배포(Released)
                                         └─ 반려(Rejected) → 재작성(Draft)
```

승인 상태 전이 규칙:
- `Pending → Approved1`: 권한그룹 `RMS_APPROVER1` 보유자만 가능
- `Approved1 → Approved`: 권한그룹 `RMS_APPROVER2` 보유자만 가능 (본인 1차 승인 불가)
- `Approved → Released`: `RMS_RELEASER` 권한 필요, 설비 다운로드 명령 전송

```csharp
public class RecipeApprovalService
{
    public async Task<ApprovalResult> ApproveAsync(
        string recipeId, string approverId, string approvalLevel)
    {
        // 권한 검증
        if (!UserInfo.Current.AuthorityList.Contains($"RMS_APPROVER{approvalLevel}"))
            throw new UnauthorizedException();

        // 자기 승인 방지 (1차 승인자 ≠ 2차 승인자)
        var recipe = await _recipeRepo.GetAsync(recipeId);
        if (approvalLevel == "2" && recipe.FirstApproverId == approverId)
            throw new SelfApprovalException();

        await _recipeRepo.UpdateApprovalStateAsync(recipeId, approverId, approvalLevel);
        return ApprovalResult.Success();
    }
}
```

### 10.6 Micube.SmartEES.Qms (품질 관리)

#### 10.6.1 화면 목록

| 화면 | 기본 클래스 | 관련 테이블 | 설명 |
|------|-------------|-------------|------|
| 불량 등록/조회 | `SmartConditionBaseForm` | `QMS_TB_DEFECT` | 불량 발생 등록 및 조회 |
| 불량 분류 마스터 | `SmartConditionBaseForm` | `QMS_TB_DEFECT_CLASS` | 불량 유형 코드 관리 |
| SPC 파라미터 설정 | `SmartConditionBaseForm` | `QMS_TB_SPC_PARAM` | 관리도 기준값 설정 |
| SPC 관리도 조회 | `SmartConditionForm` | `QMS_TB_SPC_DATA` | X-bar, R chart 표시 |
| 수율 분석 | `SmartConditionForm` | `QMS_TB_YIELD` | 공정별/설비별 수율 집계 |
| 검사 기준서 | `SmartConditionBaseForm` | `QMS_TB_INSPECTION_SPEC` | 검사 항목 및 기준값 |
| 검사 결과 | `SmartConditionBaseForm` | `QMS_TB_INSPECTION_RESULT` | 검사 수행 결과 |
| 불량 이력 | `HistoryForm` | `QMS_TB_DEFECT_HIST` | 불량 수정 이력 (SO audit) |

### 10.7 Micube.SmartEES.Ems (설비 보전)

#### 10.7.1 화면 목록

| 화면 | 기본 클래스 | 관련 테이블 | 설명 |
|------|-------------|-------------|------|
| 보전 계획 | `SmartConditionBaseForm` | `EMS_TB_PLAN` | PM 계획 등록/수정 |
| 작업 지시 | `SmartConditionBaseForm` | `EMS_TB_WORK_ORDER` | WO 발행 및 관리 |
| 작업 지시 결과 입력 | `SmartConditionBaseForm` | `EMS_TB_WORK_ORDER_RESULT` | 현장 작업 결과 등록 |
| 고장 이력 | `HistoryForm` | `EMS_TB_FAILURE_HISTORY` | 고장 발생/조치 이력 |
| 점검 체크리스트 | `SmartConditionBaseForm` | `EMS_TB_CHECKLIST` | 점검 항목별 결과 입력 |
| 예비 부품 재고 | `SmartConditionBaseForm` | `EMS_TB_SPARE_PART_STOCK` | 예비 부품 입출고 관리 |
| 계측기 교정 | `SmartConditionBaseForm` | `EMS_TB_CALIBRATION` | 교정 이력 및 예정 알림 |
| MTBF/MTTR 분석 | `SmartConditionForm` | `EMS_TB_MTBF_MTTR` | 설비 신뢰도 지표 조회 |

#### 10.7.2 예방보전 자동 알림

```csharp
// 백그라운드 서비스 — PM 예정일 도래 시 알림 생성
public class PmAlertBackgroundService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var dueItems = await _planRepo.GetDuePlansAsync(DateTime.Today.AddDays(7));
            foreach (var item in dueItems)
            {
                await _alertRepo.CreateAlertAsync(new PmAlert
                {
                    PlanId        = item.PlanId,
                    EquipmentId   = item.EquipmentId,
                    ScheduledDate = item.ScheduledDate,
                    AlertType     = "PM_DUE",
                });
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
```

### 10.8 Micube.SmartEES.Ppm (생산 계획)

#### 10.8.1 화면 목록

| 화면 | 기본 클래스 | 관련 테이블 | 설명 |
|------|-------------|-------------|------|
| 생산 계획 등록 | `SmartConditionBaseForm` | `PPM_TB_PRODUCTION_PLAN` | 월/주간 생산 계획 |
| 생산 지시 | `SmartConditionBaseForm` | `PPM_TB_PRODUCTION_ORDER` | 생산 지시 발행 |
| 능력 계획 | `SmartConditionBaseForm` | `PPM_TB_CAPACITY_PLAN` | 설비 능력 계획 |
| 자원 할당 | `SmartConditionBaseForm` | `PPM_TB_RESOURCE_ALLOCATION` | 작업자/설비 할당 |
| 생산 실적 조회 | `SmartConditionForm` | — | 계획 대비 실적 분석 |

### 10.9 Micube.SmartEES.Dlv (배송 관리)

#### 10.9.1 화면 목록

| 화면 | 기본 클래스 | 관련 테이블 | 설명 |
|------|-------------|-------------|------|
| 출하 지시 | `SmartConditionBaseForm` | `DLV_TB_DELIVERY_ORDER` | 출하/납품 지시 관리 |
| 출하 아이템 | `SmartConditionBaseForm` | `DLV_TB_DELIVERY_ITEM` | 출하 아이템별 수량 |
| 출하 이력 | `HistoryForm` | `DLV_TB_SHIPMENT_HISTORY` | 출하 완료 이력 |

---

## 7. 데이터베이스 설계

### 7.1 주요 테이블 목록

#### 시스템 테이블

| 테이블명 | 설명 | 현행 유지 |
|----------|------|-----------|
| `SYS_TB_USER` | 사용자 계정 | 유지 |
| `SYS_TB_AUTHORITY` | 권한(역할) 정의 | 유지 |
| `SYS_TB_AUTHORITY_USER` | 사용자-권한 매핑 | 유지 |
| `SYS_TB_MENU` | 메뉴 계층 구조 | 유지 |
| `SYS_TB_MENU_AUTHORITY` | 메뉴-권한 매핑 | 유지 |
| `SYS_TB_TOOLBAR` | 툴바 버튼 정의 | 유지 |
| `SYS_TB_CODE` | 공통 코드 | 유지 |
| `SYS_TB_CODE_CLASS` | 코드 분류 | 유지 |
| `SYS_TB_DICTIONARY` | 다국어 사전 | 유지 |
| `SYS_TB_MESSAGE` | 시스템 메시지 | 유지 |
| `SYS_TB_LOG` | 감사 로그 | 유지 |
| `SYS_TB_FILE` | 첨부 파일 | 유지 |
| `SYS_TB_OBJECT` | SO 오브젝트 정의 | 유지 |
| `SYS_TB_OBJECT_ATTRIBUTE` | SO 속성 정의 | 유지 |
| `SYS_TB_DATASOURCE` | 데이터소스 설정 | 유지 |
| `SYS_TB_UI` | UI 정의 | 유지 |
| `UX_PROJECT` | UX 프로젝트 | 유지 |
| `UX_PROJECTFILE` | UX 프로젝트 파일 | 유지 |

#### MDM/EPT/FDC/RMS/QMS 테이블 (현행 완전 유지)

| 테이블명 | 도메인 | 설명 |
|----------|--------|------|
| `STD_TB_PLANT` | MDM | 플랜트 |
| `STD_TB_AREA` | MDM | 구역 |
| `STD_TB_EQUIPMENT` | MDM | 설비 마스터 |
| `STD_TB_EQUIPMENT_CLASS` | MDM | 설비 분류 |
| `STD_TB_EQUIPMENT_STATE` | MDM | 설비 상태 정의 |
| `STD_TB_PROCESS` | MDM | 공정 |
| `STD_TB_PROCESS_SEGMENT` | MDM | 공정 세그먼트 |
| `STD_TB_ITEM` | MDM | 제품/자재 마스터 |
| `STD_TB_CARRIER` | MDM | 캐리어 |
| `STD_TB_VENDOR` | MDM | 협력업체 |
| `EPT_TB_EQUIPMENT_ALARM` | EPT | 설비 알람 이력 |
| `EPT_TB_EQUIPMENT_STATE_HISTORY` | EPT | 설비 상태 이력 |
| `FDC_TB_PARAMETER` | FDC | FDC 파라미터 마스터 |
| `FDC_TB_PARAMETER_GROUP` | FDC | 파라미터 그룹 |
| `FDC_TB_COLLECT_DATA` | FDC | 수집 원시 데이터 |
| `FDC_TB_INTERLOCK_RULE` | FDC | 인터락 규칙 정의 |
| `FDC_TB_INTERLOCK_HISTORY` | FDC | 인터락 발생/해제 이력 |
| `FDC_TB_ALARM_CONFIG` | FDC | FDC 알람 임계치 설정 |
| `FDC_TB_ALARM_HISTORY` | FDC | FDC 알람 발생 이력 |
| `RMS_TB_RECIPE` | RMS | 레시피 마스터 |
| `RMS_TB_RECIPE_PARAM` | RMS | 레시피 파라미터 설정값 |
| `RMS_TB_RECIPE_APPROVAL` | RMS | 레시피 승인 현황 |
| `RMS_TB_EQUIPMENT_RECIPE` | RMS | 설비-레시피 매핑 |
| `RMS_TB_DOWNLOAD_HISTORY` | RMS | 레시피 설비 전송 이력 |
| `QMS_TB_DEFECT` | QMS | 불량 이력 |
| `QMS_TB_DEFECT_CLASS` | QMS | 불량 분류 코드 |
| `QMS_TB_SPC_PARAM` | QMS | SPC 관리도 기준값 |
| `QMS_TB_SPC_DATA` | QMS | SPC 수집 데이터 |
| `QMS_TB_YIELD` | QMS | 수율 집계 |
| `QMS_TB_INSPECTION_SPEC` | QMS | 검사 기준서 |
| `QMS_TB_INSPECTION_RESULT` | QMS | 검사 수행 결과 |

#### EMS 테이블 (설비보전 — Equipment Maintenance System)

> **⚠️ 실제 소스 확인 사항:** EMS 도메인은 20개 이상의 테이블을 포함한다.

| 테이블명 | 설명 |
|----------|------|
| `EMS_TB_PLAN` | 보전 계획 (PM Plan) |
| `EMS_TB_PLAN_EQUIPMENT` | 계획별 설비 매핑 |
| `EMS_TB_PLAN_TASK` | 계획 작업 항목 |
| `EMS_TB_WORK_ORDER` | 작업 지시(WO) |
| `EMS_TB_WORK_ORDER_ITEM` | 작업 지시 항목 |
| `EMS_TB_WORK_ORDER_RESULT` | 작업 지시 결과 |
| `EMS_TB_FAILURE_CODE` | 고장 코드 마스터 |
| `EMS_TB_FAILURE_HISTORY` | 고장 이력 |
| `EMS_TB_INSPECTION_ITEM` | 점검 항목 마스터 |
| `EMS_TB_INSPECTION_RESULT` | 점검 수행 결과 |
| `EMS_TB_SPARE_PART` | 예비 부품 마스터 |
| `EMS_TB_SPARE_PART_STOCK` | 예비 부품 재고 |
| `EMS_TB_SPARE_PART_HISTORY` | 예비 부품 입출고 이력 |
| `EMS_TB_MAINTENANCE_COST` | 보전 비용 |
| `EMS_TB_CALIBRATION` | 계측기 교정 이력 |
| `EMS_TB_CALIBRATION_ITEM` | 교정 항목 마스터 |
| `EMS_TB_CHECKLIST` | 보전 체크리스트 |
| `EMS_TB_CHECKLIST_ITEM` | 체크리스트 항목 |
| `EMS_TB_PM_CYCLE` | 예방보전 주기 설정 |
| `EMS_TB_PM_ALERT` | 보전 예정 알림 |
| `EMS_TB_MTBF_MTTR` | MTBF/MTTR 집계 |

#### PPM 테이블 (생산계획 — Production Planning and Management)

| 테이블명 | 설명 |
|----------|------|
| `PPM_TB_PRODUCTION_PLAN` | 생산 계획 헤더 |
| `PPM_TB_PRODUCTION_PLAN_ITEM` | 생산 계획 아이템 |
| `PPM_TB_PRODUCTION_ORDER` | 생산 지시 |
| `PPM_TB_PRODUCTION_ORDER_ITEM` | 생산 지시 아이템 |
| `PPM_TB_CAPACITY_PLAN` | 능력 계획 |
| `PPM_TB_RESOURCE_ALLOCATION` | 자원 할당 |

#### DLV 테이블 (배송관리 — Delivery Management)

| 테이블명 | 설명 |
|----------|------|
| `DLV_TB_DELIVERY_ORDER` | 출하/납품 지시 |
| `DLV_TB_DELIVERY_ITEM` | 출하 아이템 |
| `DLV_TB_SHIPMENT_HISTORY` | 출하 이력 |

#### 감사 이력 테이블 패턴 (_HIST 접미어)

> SO `historyEnabled: true` 설정 시 원본 테이블과 동일한 구조에 감사 컬럼이 추가된 `_HIST` 테이블이 자동으로 사용된다. 이 테이블들은 DDL 마이그레이션 스크립트에서 원본 테이블과 함께 생성한다.

| _HIST 테이블 | 추가 컬럼 |
|-------------|-----------|
| `STD_TB_EQUIPMENT_HIST` | `TXN_HIST_KEY`, `HIST_TYPE`, `HIST_TIME`, `HIST_USER` |
| `RMS_TB_RECIPE_HIST` | 동일 |
| `QMS_TB_DEFECT_HIST` | 동일 |
| `EMS_TB_WORK_ORDER_HIST` | 동일 |
| `PPM_TB_PRODUCTION_ORDER_HIST` | 동일 |
| *(historyEnabled=true 모든 SO)* | 동일 패턴 |

### 7.2 데이터소스 설정 (현행 JSON 구조 유지)

```json
// Config/Datasource/mssql-datasource.json (현행 유지)
{
  "datasourceId": "default",
  "dbmsType": "MSSQL",
  "connectionString": "Server=192.168.25.102,1433;Database=PRODUCT_SMARTFACTORY_3_5;",
  "minPoolSize": 1,
  "maxPoolSize": 10,
  "validationQuery": "SELECT 1"
}
```

### 7.3 쿼리 파일 구조 (현행 XML 유지)

```xml
<!-- Config/Query/xml/{모듈명}.xml -->
<!-- 동일 Query ID에 dbms 속성으로 복수 정의 가능 — 런타임에 활성 DBMS 버전 선택 -->
<Queries>
  <!-- MSSQL 전용 버전 -->
  <Query id="com_sp_selectEquipment" version="1.0" dbms="MSSQL">
    <![CDATA[
      SELECT TOP(:pageSize) *
      FROM STD_TB_EQUIPMENT WITH (NOLOCK)
      WHERE PLANT_ID = :plantId
      #if(:equipmentId != null)
        AND EQUIPMENT_ID = :equipmentId
      #end
    ]]>
  </Query>

  <!-- PostgreSQL 전용 버전 -->
  <Query id="com_sp_selectEquipment" version="1.0" dbms="PostgreSQL">
    <![CDATA[
      SELECT *
      FROM STD_TB_EQUIPMENT
      WHERE PLANT_ID = :plantId
      #if(:equipmentId != null)
        AND EQUIPMENT_ID = :equipmentId
      #end
      LIMIT :pageSize
    ]]>
  </Query>

  <!-- DBMS 무관 공용 버전 (dbms 속성 생략) -->
  <Query id="com_sp_selectCode" version="1.0">
    <![CDATA[
      SELECT * FROM SYS_TB_CODE WHERE CODE_CLASS_ID = :codeClassId
    ]]>
  </Query>
</Queries>
```

**멀티DB 쿼리 런타임 선택 로직:**

```csharp
// SmartEES.Infrastructure/QueryRepository.cs
public string GetQuerySql(string queryId)
{
    var activeDbms = _dbConfig.DbmsType; // "MSSQL" | "PostgreSQL" | "Oracle" | "MySQL"

    // 1순위: 현재 DBMS와 일치하는 쿼리
    var match = _queryMap
        .Where(q => q.Id == queryId && q.Dbms == activeDbms)
        .FirstOrDefault();

    // 2순위: dbms 속성 없는 공용 쿼리 (fallback)
    match ??= _queryMap
        .Where(q => q.Id == queryId && string.IsNullOrEmpty(q.Dbms))
        .FirstOrDefault();

    if (match == null)
        throw new QueryNotFoundException(
            $"Query '{queryId}' not found for DBMS '{activeDbms}'");

    return match.Sql;
}
```

> **규칙:** 동일 `id`에 `dbms` 속성이 있는 버전과 없는 버전이 공존하면, 항상 **DBMS 특정 버전이 우선**한다. DBMS별 최적화가 필요없는 쿼리는 `dbms` 속성을 생략하여 단일 버전으로 유지한다.

---

## 8. 워크플로우 엔진 설계

> **v1.8 변경:** 커스텀 `WorkflowEngine` → **NexusFramework** `WorkflowManager` / `WorkflowExecutor` / `FlowExecutionEngine` 기반으로 완전 대체.  
> 비즈니스 로직은 `AssemblyInvocationNode` + `[WorkflowCallable]` 패턴으로 연결.

### 8.1 현행 구조 (Java 워크플로우)

```json
// 현행 Config/Workflow/sample.json 구조
{
  "tasks": [
    { "id": "startTask", "type": "startTask", "next": ["dataLoad"] },
    { "id": "dataLoad", "type": "templateTask",
      "destination": "/api/load", "next": ["splitData"] },
    { "id": "endTask", "type": "endTask" }
  ]
}
```

### 8.2 타겟 C# 구현 — NexusFramework 기반

#### 핵심 클래스 구조 (submodules/NexusFramework/src/NexusFramework/Workflow/)

```
WorkflowManager (Tooling/WorkflowManager.cs)
  └─ 다중 WorkflowExecutor 관리, 동시성 제어, 로드밸런싱
      └─ WorkflowExecutor (Tooling/WorkflowExecutor.cs)
            └─ .workflow / .aigraph.json 파일 로드 + FlowExecutionEngine 실행
                 └─ FlowExecutionEngine (Runtime/FlowExecutionEngine.cs)
                      └─ FlowDocument (Graph/FlowDocument.cs) 그래프 실행
                           └─ INode.ExecuteAsync(NodeContext, CancellationToken)
                                └─ AssemblyInvocationNode (Nodes/AssemblyInvocationNode.cs)
                                     └─ [WorkflowCallable] 메서드 리플렉션 호출
```

#### 현행 Java 개념 → NexusFramework 매핑표

| 현행 Java 개념 | NexusFramework 대응 | 비고 |
|----------------|---------------------|------|
| `WorkflowDefinition` | `Workflow` + `FlowDocument` | JSON 직렬화 가능 |
| `WorkflowTask` | `NodeDefinition` | id, type, properties, ports |
| `tasks[].next[]` (순서 연결) | `EdgeDefinition` (From→To 엣지) | 그래프 기반 |
| `startTask` | 의존성이 없는 첫 번째 NodeDefinition | 진입 노드 |
| `endTask` | 의존하는 노드가 없는 마지막 NodeDefinition | 종료 노드 |
| `templateTask` | `AssemblyInvocationNode` (type: `"AssemblyInvocation"`) | 어셈블리 메서드 호출 |
| `splitTask` (병렬) | 팬아웃 엣지 (한 노드 → 여러 노드 엣지) | 자동 병렬 실행 |
| `joinTask` (동기화) | 팬인 의존성 (여러 노드 → 한 노드) | 자동 완료 대기 |
| `WorkflowContext.SharedData` | `NodeContext.TryGetResult(nodeId, out result)` | 이전 노드 결과 참조 |
| `WorkflowResult` | `FlowExecutionReport` | results: Dict<nodeId, NodeResult> |

#### 워크플로우 파일 형식 (*.workflow / *.aigraph.json)

```json
// Config/Workflow/RecipeApproval.workflow
{
  "version": "1.0",
  "properties": { "id": "RecipeApproval", "name": "레시피 승인 워크플로우" },
  "graph": {
    "nodes": [
      {
        "id": "validateRecipe",
        "type": "AssemblyInvocation",
        "properties": {
          "assemblyPath": "./Modules/Micube.SmartEES.Rms.dll",
          "typeName": "SmartEES.Application.Rules.RecipeValidationService",
          "methodName": "ValidateAsync",
          "arguments": { "recipeId": "{{recipeId}}" }
        }
      },
      {
        "id": "sendApprovalRequest",
        "type": "AssemblyInvocation",
        "properties": {
          "assemblyPath": "./Modules/Micube.SmartEES.Rms.dll",
          "typeName": "SmartEES.Application.Rules.RecipeApprovalService",
          "methodName": "RequestApprovalAsync",
          "arguments": { "recipeId": "{{recipeId}}", "requesterId": "{{userId}}" }
        }
      },
      {
        "id": "notifyApprovers",
        "type": "AssemblyInvocation",
        "properties": {
          "assemblyPath": "./Modules/Micube.SmartEES.Rms.dll",
          "typeName": "SmartEES.Application.Rules.NotificationService",
          "methodName": "SendApprovalNotificationAsync"
        }
      }
    ],
    "edges": [
      { "id": "e1", "from": { "nodeId": "validateRecipe", "port": "out" },     "to": { "nodeId": "sendApprovalRequest", "port": "in" } },
      { "id": "e2", "from": { "nodeId": "sendApprovalRequest", "port": "out" }, "to": { "nodeId": "notifyApprovers", "port": "in" } }
    ]
  }
}
```

#### DI 등록 및 WorkflowManager 사용

```csharp
// SmartEES.API/Program.cs
// WorkflowManager + WorkflowExecutor + AssemblyInvocationNode 등록
builder.Services.AddSingleton<INodeRegistry>(sp =>
{
    var registry = new NodeRegistry();
    registry.RegisterAssemblyInvocationNode();    // AssemblyInvocationNode 등록
    return registry;
});
builder.Services.AddSingleton(sp =>
{
    var registry = sp.GetRequiredService<INodeRegistry>();
    var executor = new WorkflowExecutor(registry);
    return new WorkflowManager(executor);         // 단일 Executor
});

// 워크플로우 실행 예시 (API Controller)
public class WorkflowController : ControllerBase
{
    private readonly WorkflowManager _manager;

    [HttpPost("execute/{workflowId}")]
    public async Task<IActionResult> Execute(string workflowId,
        [FromBody] Dictionary<string, object> parameters,
        CancellationToken ct)
    {
        var workflowPath = Path.Combine(_workflowDir, $"{workflowId}.workflow");
        var options = new FlowExecutionOptions
        {
            Services = HttpContext.RequestServices,   // DI 컨테이너 → 노드에 주입
            MaxParallelism = 4
        };

        var reports = await _manager.ExecuteAsync(workflowPath, options, ct);
        var report  = reports.First();

        return report.HasErrors
            ? BadRequest(new { errors = report.Errors })
            : Ok(new { results = report.Results.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Outputs) });
    }
}
```

#### NodeContext — 노드 간 데이터 전달

```csharp
// AssemblyInvocationNode가 실행하는 [WorkflowCallable] 메서드에서 이전 노드 결과 참조
[WorkflowCallable(DisplayName = "Recipe Approve")]
public async Task<RuleResult> RequestApprovalAsync(
    string recipeId,
    NodeContext nodeContext,    // NexusFramework 자동 주입
    IServiceProvider services, // DI 컨테이너
    CancellationToken ct)
{
    // 이전 노드(validateRecipe) 실행 결과 조회
    if (nodeContext.TryGetResult("validateRecipe", out var validationResult))
    {
        var isValid = validationResult.Outputs.TryGetValue("isValid", out var v) && (bool)v!;
        if (!isValid) return RuleResult.Fail("검증 실패");
    }

    var repo = services.GetRequiredService<IRecipeRepository>();
    await repo.UpdateStateAsync(recipeId, "PENDING");
    return RuleResult.Success(new { approvalId = Guid.NewGuid() });
}
```

#### FlowExecutionReport — 실행 결과

```csharp
// FlowExecutionEngine 실행 완료 후 결과
var report = await _manager.ExecuteAsync(workflowPath, options, ct);

// 성공/실패 판단
bool success = !report.HasErrors;

// 오류 메시지 목록
IReadOnlyList<string> errors = report.Errors;

// 노드별 실행 결과
foreach (var (nodeId, result) in report.Results)
{
    if (result.Status == NodeExecutionStatus.Succeeded)
    {
        var output = result.Outputs["returnValue"];
    }
    else if (result.Status == NodeExecutionStatus.Failed)
    {
        var errorMsg = result.Error;
    }
    else if (result.Status == NodeExecutionStatus.Skipped)
    {
        // TryCatch 스코프에서 오류 없어 Catch 노드 스킵됨
    }
}
```

#### 현행 websocket-events.xml → NexusFramework 매핑

| 현행 이벤트 타입 | NexusFramework 대응 | 구현 방식 |
|-----------------|---------------------|-----------|
| `startTask` | 진입 NodeDefinition | 의존성 없는 첫 번째 노드 |
| `templateTask` | `AssemblyInvocationNode` | `[WorkflowCallable]` 메서드 호출 |
| `endTask` | 최종 NodeDefinition | 다른 노드가 의존하지 않는 마지막 노드 |
| `splitTask` (병렬) | 팬아웃 엣지 | 한 노드 → 여러 엣지 → 병렬 자동 실행 |
| `joinTask` (동기화) | 팬인 의존성 | 여러 노드 → 한 엣지 → 완료 대기 |
| `try/catch 오류처리` | `TryNode`/`CatchNode`/`FinallyNode` | 스코프 기반 오류 처리 내장 |

#### Try/Catch/Finally 스코프 (내장 기능)

```json
// NexusFramework 내장 TryCatch 노드 타입 활용
{
  "nodes": [
    { "id": "try1",     "type": "try",     "properties": { "scopeId": "approval" } },
    { "id": "approve",  "type": "AssemblyInvocation", "properties": { "scopeId": "approval", ... } },
    { "id": "catch1",   "type": "catch",   "properties": { "scopeId": "approval", "continueOnCatch": false } },
    { "id": "rollback", "type": "AssemblyInvocation", "properties": { "scopeId": "approval", ... } },
    { "id": "finally1", "type": "finally", "properties": { "scopeId": "approval" } },
    { "id": "cleanup",  "type": "AssemblyInvocation", "properties": { "scopeId": "approval", ... } }
  ]
}
```

---

## 9. 보안 / 인증 / 권한 설계

### 9.1 인증 흐름

> 세션 만료, 비밀번호 변경, 강제 로그아웃, 재로그인 팝업 상세 흐름은 **섹션 19.1** 참조.  
> 비밀번호 정책(복잡도/만료/이력) 상세는 **섹션 19.2** 참조.

```
[로그인]
Client → POST /api/auth/login  { userId, password(SHA256), plantId, languageType }
       → 서버: SYS_TB_USER 조회 + 비밀번호 검증 + SaveConnectionHistory
       → 성공: { accessToken(8시간), refreshToken(7일), userInfo, authorityList }
       → 클라이언트: TokenStore에 저장, UserInfo.Current 설정

[API 요청]
Client → Authorization: Bearer {accessToken}

[토큰 갱신 — Access Token 만료 전 자동 갱신]
Client → POST /api/auth/refresh  { refreshToken }
       → 서버: Refresh Token 유효성 검증 (DB 또는 캐시)
       → 성공: 새 accessToken + 새 refreshToken (Rotation)

[로그아웃]
Client → POST /api/auth/logout  { refreshToken }
       → 서버: Refresh Token 무효화, SetLogoutTime()
       → 클라이언트: UserInfo.Clear(), TokenStore.Clear()
```

**인증 API 엔드포인트 목록:**

| Method | Path | 설명 |
|--------|------|------|
| POST | `/api/auth/login` | 로그인, JWT 발급 |
| POST | `/api/auth/logout` | 로그아웃, Refresh Token 무효화 |
| POST | `/api/auth/refresh` | Access Token 갱신 (Refresh Token 사용) |
| POST | `/api/auth/change-password` | 로그인 중 비밀번호 변경 |
| POST | `/api/auth/reset-password` | 비밀번호 분실/초기화 (이메일 인증) |
| GET  | `/api/auth/me` | 현재 로그인 사용자 정보 조회 |

### 9.2 JWT 설정

```csharp
// Program.cs
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = "SmartEES",
            ValidateAudience         = true,
            ValidAudience            = "SmartEES.Client",
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(secretKey)),
            // 서버 시간 차이로 인한 즉시 만료 방지 (권장: 0)
            ClockSkew                = TimeSpan.Zero,
        };
        // SignalR 연결 시 쿼리 스트링 토큰 지원 (?access_token=...)
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });
```

### 9.3 권한 체계 (현행 유지)

```
SYS_TB_AUTHORITY (역할 정의)
    └─ AUTHORITYID: ADMIN, OPERATOR, VIEWER, ...
    
SYS_TB_AUTHORITY_USER (사용자-역할 매핑)
    └─ USERID + AUTHORITYID
    
SYS_TB_MENU_AUTHORITY (메뉴-역할 접근 설정)
    └─ MENUID + AUTHORITYID + PERMISSION (READ/WRITE/ADMIN)
    
SYS_TB_TOOLBAR (툴바 버튼 권한)
    └─ TOOLBARID + AUTHORITYID
```

### 9.4 클라이언트 권한 적용

```csharp
// SmartBaseForm에서 메뉴 로딩 시 권한 체크
public class MenuRepository : IMenuRepository
{
    public List<MenuItem> GetAuthorizedMenus(string userId)
    {
        // SYS_TB_MENU + SYS_TB_MENU_AUTHORITY JOIN
        // 현재 사용자의 권한으로 접근 가능한 메뉴만 반환
    }
}

// 툴바 버튼 권한 제어 (현행 SYS_TB_TOOLBAR 기반)
public class ToolbarAuthorizationService
{
    public bool HasPermission(string toolbarId, string authorityId)
    public void ApplyToToolbar(SmartConditionBaseForm form, string menuId)
}
```

### 9.5 JWT Refresh Token 관리

```csharp
// Refresh Token은 DB 또는 Redis에 저장하여 무효화 지원
public class RefreshTokenStore
{
    // 발급: 로그인 시 생성, SHA256 해시 저장
    public async Task<string> IssueAsync(string userId, TimeSpan expiry)

    // 검증: 존재 여부 + 만료 여부 + 폐기 여부
    public async Task<RefreshTokenValidation> ValidateAsync(string token)

    // Rotation: 갱신 시 기존 토큰 폐기 + 신규 발급
    public async Task<string> RotateAsync(string oldToken)

    // 폐기: 로그아웃 또는 강제 만료 시
    public async Task RevokeAsync(string token)
    public async Task RevokeAllByUserAsync(string userId)
}

// appsettings.json에 추가
// "Jwt": { "RefreshTokenExpirationDays": 7 }
```

### 9.6 CORS 정책

> **⚠️ 적용 범위 주의:**  
> **WinForms 클라이언트(SmartEES.App)는 브라우저가 아니므로 CORS 정책이 적용되지 않는다.**  
> CORS는 브라우저 기반 웹 클라이언트(HTML5/JS)가 다른 출처의 API를 호출할 때만 브라우저가 강제하는 보안 정책이다.  
> WinForms는 `HttpClient`를 직접 사용하므로 Origin 헤더 없이 API를 자유롭게 호출한다.  
> 따라서 아래 CORS 설정은 **웹 브라우저 클라이언트 전용**이며, WinForms 동작에는 영향이 없다.

```csharp
// SmartEES.API/Program.cs
// ※ WinForms 클라이언트는 브라우저가 아니므로 이 설정의 영향을 받지 않음
// ※ 웹 브라우저 기반 HTML5/JS 클라이언트 및 SignalR 웹소켓 연결에만 적용됨
builder.Services.AddCors(options =>
{
    options.AddPolicy("SmartEES", policy =>
    {
        policy.WithOrigins(
                builder.Configuration.GetSection("Cors:AllowedOrigins")
                       .Get<string[]>() ?? Array.Empty<string>())
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();  // SignalR 웹소켓 연결을 위해 필요
    });
});

app.UseCors("SmartEES");
```

```json
// appsettings.json
"Cors": {
  "AllowedOrigins": [
    "http://localhost:5000",         // 개발: 웹 클라이언트 devserver
    "https://smartees.company.com"   // 운영: 웹 클라이언트 도메인
    // WinForms 클라이언트 URL은 Origin이 없으므로 여기에 등록 불필요
  ]
}
```

> **Rate Limiting / Circuit Breaker** 설계는 **섹션 18.2** 참조.

---

## 11. 설정 및 인프라 설계

### 11.1 App.yml (클라이언트 설정, 현행 구조 유지)

```yaml
Application:
  Uiid: SmartEES
  Language: ko-KR
  Plant: DEFAULT

Network:
  Main:
    Assembly: Micube.Framework.Net.Http
    Directory: ./
    Type: HttpChannel
    Url: http://localhost:8080

DLL:
  Path: ./Modules/

Log:
  Level: Info
  Path: ./Log/
  MaxFileSizeMB: 10
  MaxBackupFiles: 30
```

### 11.2 appsettings.json (서버 설정)

```json
{
  "ConnectionStrings": {
    "Default": "Server=...;Database=PRODUCT_SMARTFACTORY_3_5;"
  },
  "Jwt": {
    "SecretKey": "...",
    "Issuer": "SmartEES",
    "Audience": "SmartEES.Client",
    "ExpirationHours": 8,
    "RefreshTokenExpirationDays": 7
  },
  "Kafka": {
    "BootstrapServers": "localhost:9092",
    "GroupId": "smartees-consumer",
    "Topics": {
      "EquipmentState": "equipment.state.changed",
      "AlarmEvent": "equipment.alarm.fired",
      "FdcRawData": "fdc.rawdata"
    }
  },
  "Redis": {
    "ConnectionString": "localhost:6379",
    "InstanceName": "SmartEES:",
    "DefaultExpirationMinutes": 30
  },
  "Cors": {
    "AllowedOrigins": [
      "http://localhost:5000",
      "https://smartees.company.com"
    ]
  },
  "HealthChecks": {
    "Path": "/health",
    "ReadinessPath": "/health/ready",
    "LivenessPath": "/health/live"
  },
  "QueryPath": "./Config/Query/xml/",
  "WorkflowPath": "./Config/Workflow/",
  "SignalR": {
    "MaximumReceiveMessageSize": 102400
  },
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "File", "Args": { "path": "./Logs/log-.txt", "rollingInterval": "Day" } }
    ]
  }
}
```

### 11.3 이메일 서비스 (현행 Config/Mail/*.xml 유지)

```csharp
public class MailService
{
    // 현행 XML 템플릿 구조 유지
    // ChangePassword(en-US).xml, InitPassword(ko-KR).xml
    public async Task SendPasswordChangeNotification(
        string userId, string email, string languageType)
    public async Task SendPasswordInitNotification(
        string userId, string email, string languageType)
    public async Task SendFdcInterlockNotification(
        string equipmentId, string paramName)
}
```

---

## 12. 마이그레이션 단계별 계획

### Phase 1: 기반 인프라 구축 (2~3주)

| 작업 | 설명 |
|------|------|
| 솔루션 생성 | SmartEES.sln 및 프로젝트 골격 생성 |
| Framework 업그레이드 | .NET 4.5 → .NET 8 마이그레이션 |
| Micube.Framework | AppConfiguration, EventAggregator, Language 이전 |
| Micube.Framework.Net | MessageWorker, SqlExecuter, ChannelProxy 이전 |
| SmartEES.API | ASP.NET Core Web API 기본 구조 구축 |
| DB 연결 | Dapper + QueryRepository 구현 |
| 인증 | JWT 기반 로그인 API 구현 |

### Phase 2: UI 프레임워크 이전 (4~5주)

| 작업 | 설명 |
|------|------|
| SmartControls 이전 | SmartBaseForm, SmartConditionBaseForm, SmartConditionForm 이전 |
| 팝업 폼 계층 | SmartPopupBaseForm, SmartPopupCheckGridForm, SmartPopupMultiGridForm 이전 |
| 이력 폼 | HistoryForm 이전 (SO _HIST 연동 포함) |
| SmartUserControl | UserControl 기반 재사용 위젯 이전 |
| SmartBandedGrid | DevExpress 24.x 호환 업그레이드 |
| ConditionCollection | 검색 조건 13종 컨트롤 이전 |
| SmartChart | 차트 컴포넌트 이전 |
| LoginForm | 로그인 화면 이전 (3개 InjectModule 부트스트랩 포함) |
| MainForm | 메인 윈도우 이전 |
| FormCreator | 동적 DLL 로딩 팩토리 이전 |

### Phase 3: 백엔드 비즈니스 로직 이전 (8~10주)

| 작업 | 설명 |
|------|------|
| MDM 모듈 | 설비/제품/공정/코드/사용자클래스 마스터 (우선 이전) |
| SystemManagement | 사용자/권한/메뉴 관리 |
| EPT 모듈 | 설비 성능 추적, 알람/상태/Loss/OEE |
| FDC 모듈 | 설비 데이터 수집, 인터락 규칙 엔진, Kafka Consumer |
| RMS 모듈 | 레시피 관리, 승인 워크플로우 |
| QMS 모듈 | 품질 관리, SPC, 수율 분석 |
| EMS 모듈 | 설비 보전(PM/WO/점검/부품/교정), MTBF/MTTR 분석 |
| PPM 모듈 | 생산 계획, 생산 지시, 능력 계획 |
| DLV 모듈 | 출하 지시 및 배송 관리 |
| 워크플로우 | C# 워크플로우 엔진 구현 |
| 메시지/알림 | SignalR + Kafka.NET |

### Phase 4: 통합 검증 및 Java 제거 (2~3주)

| 작업 | 설명 |
|------|------|
| 통합 테스트 | 모든 화면 기능 동일성 검증 |
| 성능 테스트 | 현행 대비 성능 지표 비교 |
| UAT | 사용자 수용 테스트 |
| Java 시스템 종료 | 검증 완료 후 Java 백엔드 중지 |

### 12.5 테스트 전략

#### 단위 테스트 (SmartEES.UnitTests)

| 대상 | 검증 내용 | 도구 |
|------|-----------|------|
| `VelocityTemplateProcessor` | `#if`, `#foreach`, `:변수` 변환 정확성 | xUnit |
| `ServiceObjectProcessor` | 감사 필드 자동 주입, `_HIST` 복사 | xUnit + Moq |
| `RuleRegistry` | 룰 등록/조회/실행 | xUnit |
| `WorkflowEngine` | 태스크 순서, Rollback 동작 | xUnit |
| `RefreshTokenStore` | Rotation, Revoke, 만료 검증 | xUnit |
| `Language.Get()` | 다국어 키 조회, fallback | xUnit |
| `ConditionCollection.GetValues()` | 필수조건 누락 검증 | xUnit |

#### 통합 테스트 (SmartEES.IntegrationTests)

| 대상 | 검증 내용 | 도구 |
|------|-----------|------|
| `AuthController` | 로그인/로그아웃/Refresh Token 흐름 | xUnit + WebApplicationFactory |
| `RuleController` | 실제 DB에 대한 쿼리/프로시저 실행 | xUnit + TestContainers(MSSQL) |
| `QueryRepository` | XML 쿼리 로딩 + Dapper 실행 | xUnit + TestContainers |
| `SO 감사 메커니즘` | INSERT → `_HIST` 자동 복사 검증 | xUnit + TestContainers |
| `KafkaToSignalRBridge` | Kafka 메시지 → SignalR 전파 | xUnit + Testcontainers(Kafka) |

#### UI 회귀 테스트 (SmartEES.UITests)

| 대상 | 검증 내용 | 도구 |
|------|-----------|------|
| LoginForm | 로그인 성공/실패, 비밀번호 변경 팝업 | WinForms UI Automation |
| Equipment 화면 | 검색 조건 → 그리드 결과 → 저장 골든 패스 | WinForms UI Automation |
| 언어 변경 | ko-KR / en-US 전환 후 Caption 검증 | WinForms UI Automation |

#### 성능 기준 (Java 대비)

| 지표 | 목표 | 측정 방법 |
|------|------|-----------|
| API 응답 시간 (P95) | ≤ 현행 Java × 0.8 | k6 부하 테스트 |
| 화면 로딩 시간 | ≤ 2초 (현행 동일) | 수동 측정 |
| Kafka → SignalR 지연 | ≤ 500ms | 타임스탬프 비교 |
| DB 쿼리 (Top 10 slow) | ≤ 현행 × 1.0 | Dapper + Serilog 측정 |

---

## 13. 기술 스택 매핑표

| 현행 | 타겟 | 비고 |
|------|------|------|
| .NET Framework 4.5 | .NET 8 LTS | 장기 지원 버전 |
| DevExpress 18.1 | DevExpress 24.1 | 최신 버전 |
| Ninject 3.x | Ninject 4.x / Microsoft.Extensions.DI | DI 컨테이너 |
| Newtonsoft.Json | System.Text.Json / Newtonsoft.Json | 하이브리드 |
| YamlDotNet | YamlDotNet 15.x | 업그레이드 |
| AutoMapper 6.x | AutoMapper 13.x | 업그레이드 |
| WCF 클라이언트 | HttpClient (System.Net.Http) | WCF 제거 |
| Java OSGi 백엔드 | ASP.NET Core 8 Web API | 완전 교체 |
| Java Kafka Producer | Confluent.Kafka (.NET) | 교체 |
| Java WebSocket | ASP.NET Core SignalR | 교체 |
| Java JDBC/SO 패턴 | Dapper + ServiceObjectProcessor | 교체 |
| Java Rule 엔진 | C# IRule + RuleRegistry | 재구현 |
| Jetty 웹서버 | Kestrel (ASP.NET Core) | 교체 |
| Log4J | Microsoft.Extensions.Logging + Serilog | 교체 |
| Java JWT | System.IdentityModel.Tokens.Jwt | 교체 |

---

## 14. 소스 검증 기반 보완 상세 설계

본 장은 `reference/SmartUX3.5_20260526`의 실제 C# 클라이언트, Java Rule/Communication, Query XML, Schema SQL을 대조하여 기존 상세설계에서 누락되었거나 구현 수준이 부족한 항목을 보완한 것이다. 구현 시 본 장의 목록을 마이그레이션 대상 기준선으로 사용한다.

### 14.1 누락/보완 요약

| 점검 항목 | 소스 검증 결과 | 보완 설계 |
|------|------|------|
| 도메인 화면 설계 | 기존 문서는 MDM/EPT/SystemManagement 일부 화면 위주이며 FDC, RMS, QMS, 팝업 화면 목록이 부족함 | 14.2, 14.4에 실제 화면/팝업 기준 마이그레이션 목록 추가 |
| SmartControls 개별 컨트롤 | 기존 문서는 SmartGrid 중심이며 Chart, PivotGrid, Dialog, Popup, Excel, TreeList, Condition 계열이 부족함 | 14.3에 컨트롤별 책임, C# 이전 대상 API, 검증 기준 추가 |
| 알람/FDC/레시피 룰 매핑 | Java Rule/Communication 모듈과 C# 서비스 매핑이 구체화되지 않음 | 14.5에 Rule/Communication → C# Application Service/Handler 매핑 추가 |
| 다국어/다중 플랜트 | `LanguageType`, `PlantId`, 사용자별 Plant Map, `_TXNINFO.LANGUAGETYPE` 처리 상세가 부족함 | 14.6에 컨텍스트 전파, 쿼리 필터, UI 언어 변경 설계 추가 |
| 에러 처리/예외 전파 | `Response.Success`, `GetFailMessage`, `MSGBox`, `UIHelper.ShowError`의 이전 기준이 부족함 | 14.7에 API 표준 오류 계약, 클라이언트 예외 매핑, 로깅 설계 추가 |
| Excel 가져오기/내보내기 | `ExcelImportDialog`, Grid Export, Chart Image Export 설계가 누락됨 | 14.8에 Import Wizard, Grid Export, 검증/오류 표시 설계 추가 |
| 팝업 폼 | `SmartPopupBaseForm`, `ISmartCustomPopup`, 도메인 팝업 목록이 누락됨 | 14.4에 팝업 공통 계약과 모듈별 목록 추가 |
| 설정 관리 | 로그인, 즐겨찾기, 최근메뉴, 조건저장 저장소 설계가 부족함 | 14.9에 로컬 JSON/서버 저장 방식과 마이그레이션 규칙 추가 |
| 모바일 알림 | `SYS_TB_MOBILE_NOTIFICATION`, `SYS_TB_MOBILE_NOTIFICATION_USERS` 설계가 누락됨 | 14.10에 테이블, 서비스, 전송 상태 관리 추가 |
| 기타 중요 항목 | Scheduler, 승인, 실시간 데이터, 파일/첨부, DBMS별 Query, 감사 컬럼 이전 기준이 부족함 | 14.11~14.12에 추가 보완 |

### 14.2 도메인 모듈별 화면 보완 설계

도메인 UI는 기존 WinForms/DevExpress 화면을 기능 단위로 이전한다. 각 화면은 `SmartConditionBaseForm` 기반 검색 조건, `SmartBandedGrid` 또는 전용 컨트롤, `SqlExecuter` 조회, `MessageWorker`/Rule 저장 호출 구조를 보존하되, 타겟에서는 `IQueryService`, `IRuleClient`, `IMessageService`로 추상화한다.

#### 14.2.1 MDM 화면 보완

기존 MDM 설계에는 설비/제품/공정 일부만 반영되어 있으므로 다음 화면을 마이그레이션 범위에 포함한다.

| 분류 | 화면/클래스 | 이전 설계 |
|------|------|------|
| 기준 코드 | `Code`, `CodeClass` | 코드 클래스/코드 CRUD, 다국어 명칭, 유효 상태, 정렬 순서 보존 |
| 공장/영역 | `Plant`, `Area` | Plant별 Area 조회/저장, 사용자 Plant 권한 필터 적용 |
| 제품/공정 | `Product`, `ProcessSegment` | 제품/공정 기준 정보와 유효 상태 이력 처리 |
| 설비 기준 | `Equipment`, `EquipmentClass`, `EquipmentClassEquipmentMapping` | 설비 트리/클래스 매핑, 설비별 Plant/Area 필터, 팝업 선택 연계 |
| 설비 알람/이벤트 | `EquipmentAlarm`, `EquipmentEvent`, `EquipmentState`, `EquipmentStateMatrix` | EPT 알람/상태/이벤트 룰과 공유되는 마스터 데이터로 관리 |
| 상태 매핑 | `EquipmentStateAlarmMapping`, `EquipmentStateEventMapping` | 설비 상태와 알람/이벤트 간 매핑 유효성 검증 |
| 메일링 | `Mailing`, `MailingUserMapping`, `UserMailingMapping` | 사용자/메일링 그룹 매핑, 알림 대상 조회 연계 |
| 사용자 클래스 | `UserClass`, `UserClassUser`, `UserClassMailingMapping` | 권한/메일링/승인 대상 그룹 연계 |
| 설비 메일링 | `UserEquipmentMailingMapping`, `UserEquipmentAlarmMailingMapping` | 설비 또는 설비 알람별 수신자 설정 |

#### 14.2.2 EPT 화면 보완

EPT는 설비 상태, 알람, Loss, OEE, Layout, MCC를 함께 제공한다. 기존 설계의 화면 목록을 다음 기준으로 확장한다.

| 분류 | 화면/클래스 | 이전 설계 |
|------|------|------|
| 설비 상태 | `EquipmentStateMonitoring`, `EquipmentStateChange`, `EquipmentStateChangePopup`, `EquipmentStateUpdatePopup` | 상태 변경, 실시간 모니터링, 상태 색상 Pivot 조회를 `EquipmentStateService`로 통합 |
| 설비 알람 | `EquipmentAlarmHistory`, `EquipmentAlarmHistoryPopup`, `EquipmentAlarmWorst10Analysis` | `EPT_TB_ALARM`, `COM_TB_ALARM_DEF`, 알람 통신 룰을 연결해 이력/분석 제공 |
| Loss 분석 | `EquipmentLossAnalysis`, `EquipmentLossAlarmHistory`, `EquipmentLossAlarmHistory_Popup`, `EquipmentLossEventHistory`, `EquipmentLossManualInput`, `EquipmentLossStateHistory`, `EquipmentLossWorst5Analysis` | Loss 원인, 수동 입력, 상태/알람/이벤트 연계 분석 유지 |
| 가동률/OEE | `EquipmentAvailabilityHistory`, `EquipmentAvailabilityHistoryPopup`, `OverallEquipmentEffectiveness`, `OverallEquipmentEffectiveness_Modify`, `OverallEquipmentEffectiveness_Popup`, `OverallEquipmentKpi` | 설비/지표별 Line/Grid 조회와 일 단위 집계 테이블 연계 |
| 지표 | `Index`, `InterestedIndex`, `InterestedIndexView`, `IndexFormulaTextPopup`, `InterestedSelectIndexPopup`, `InterestedSelectEqp_Popup` | 지표 수식 실행 `ExecuteFormula`, 관심 지표 저장 `SaveInterestIndex` 이전 |
| Layout | `FactoryMonitoring`, `FactoryMonitoringEquipmentPopup`, `Layout`, `LayoutEdit` | Layout 데이터 저장 `SaveLayout`, `SaveLayoutData`, 설비 위치/상태 표시 이전 |
| MCC | `MachineCycleChartHistory`, `MachineCycleChartItem`, `MachineCycleChartSpec`, `MachineCycleChartValidationHistory`, `MachineCycleChartValidationHistory_Popup`, `MccCopyPopup` | MCC Spec/Validation/Action 이력과 Interlock 통신 룰 연계 |
| 속성 | `EquipmentEPTProperty` | 설비별 EPT 속성 저장 `SaveEquipmentProperty` 및 이력 테이블 연계 |

#### 14.2.3 FDC 화면 보완

FDC는 Parameter, Spec, Trace, Summary, Interlock, SPC, 실시간 모니터링이 함께 동작한다. 다음 화면을 C# 마이그레이션 대상에 포함한다.

| 분류 | 화면/클래스 | 이전 설계 |
|------|------|------|
| Parameter 기준 | `TraceParameter`, `EventParameter`, `VirtualParameter`, `VirtualEventParameter`, `SummaryParameter` | `FDC_TB_PARAMETER`, `FDC_TB_SUMMARY_PARAMETER`, Virtual/Event Parameter 저장 룰로 분리 |
| Spec 관리 | `ActiveParameterSpec`, `IdleParameterSpec`, `SummaryParameterSpec` | Spec 저장/복사 룰, Hist 테이블, CPK/Chart 팝업을 포함 |
| 관심/그룹 | `InterestParameterRegist`, `TraceGroup`, `TraceGroupParameterRegist`, `ParameterStateCondition` | 관심 그룹, Trace 그룹, 상태 조건 매핑 저장 |
| 이력 조회 | `FDCParameterHistory`, `FDCParameterHistoryData`, `FDCSummaryParameterHistory`, `FDCInterestParameterHistory`, `FDCLotTrace`, `FDCLotTrend` | Trace/Event/Summary 데이터 이력 조회, Lot 기준 추적 제공 |
| Interlock | `FDCInterlockHistory`, `FDCInterlockForceCancel` | `FDC_TB_INTERLOCK_HIST`, `REQUEST_FDC_INTERLOCK`, `REPLY_FDC_INTERLOCK` 연계 |
| 실시간 모니터링 | `FDCTraceParaMonitoring`, `RealTimeTraceParaMonitoring` | `RealTimeDataCollection`, `RealTimeTraceParameterDataSourceManager`를 SignalR/Kafka 소비자로 이전 |
| 분석/모델링 | `AfterPMFDCDataAnalysis`, `EquipmentEquivalenceVarification`, `SPCModeling` | SmartChart/AdvancedChart와 SPC Rule 팝업, CPK 분석 포함 |
| 속성 | `EquipmentFDCProperty` | 설비별 FDC 속성과 Parameter 수집 조건 관리 |

FDC 전용 컨트롤(`ChartLeftMenu`, `ChartTopMenu`, `LineChartPanel`, `SmartAdvancedChart`, `SmartAdvancedChart2`, `SmartCellPanel`, `SmartRowColumnPanel`, `FdcChart`, `FdcMenuChart`)은 일반 `SmartChart`로 단순 치환하지 않는다. Chart Panel 구성, 메뉴, 축 확대/축소, Trace/Spec Overlay, 선택 포인트 Highlight를 보존하는 `FdcChartControl` 어댑터를 별도 구현한다.

#### 14.2.4 RMS 화면 보완

RMS는 공정 레시피, Sequence 레시피, 장비 레시피, 승인, 다운로드/업로드 이력이 결합되어 있으므로 다음 화면을 모두 이전한다.

| 분류 | 화면/클래스 | 이전 설계 |
|------|------|------|
| 레시피 기준 | `ProcessRecipe`, `SequenceRecipe`, `RecipeParameter`, `RecipeMapping` | 레시피 기준/파라미터/Sequence Map CRUD, 복사/업로드 팝업 포함 |
| 장비 레시피 | `EquipmentRecipeView`, `EquipmentRecipeChangeHistory`, 장비 레시피 Upload 흐름 | 장비 업로드/다운로드, 장비 레시피 변경 이력과 통신 룰 연결 |
| 승인 | `RecipeApproval` | `RequestApproval`, `ChangeApprovalState`, Approval Path 팝업과 상태 전이 구현 |
| 비교/검증 | `RecipeCompareView`, `RecipeValidationHistory` | Process/Sequence Recipe 비교 팝업, Validation 이력 조회 |
| 변경/다운로드 이력 | `RecipeChangeHistory`, `RecipeDownloadHistory` | 변경 상세 팝업, 다운로드 파라미터/Sequence 이력 조회 |
| 속성 | `EquipmentRMSProperty` | 설비별 RMS 속성, Recipe Mode, Mapping 검증 규칙 관리 |
| 관리 화면 | `RecipeManagement/ManagementRecipe` | 레시피 등록/변경/승인/배포 업무 흐름 통합 |

#### 14.2.5 SystemManagement 화면 보완

SystemManagement는 동적 메뉴/권한/조건/툴바/서비스/배포/스케줄러를 관리한다. 다음 화면은 C# 프레임워크 동작에 직접 영향을 주므로 우선 이전 대상이다.

| 분류 | 화면/클래스 | 이전 설계 |
|------|------|------|
| 사용자/권한 | `User`, `Authority`, `AuthorityUser`, `MenuAuthority`, `ToolbarAuthority`, `UserRequestApproval` | 사용자별 메뉴/툴바 권한, 승인 요청 관리 |
| 메뉴/오브젝트 | `Menu`, `Object`, `MenuToolbarMapping`, `MenuConditionItemMapping` | 메뉴 실행, 조건 생성, 툴바 구성의 메타 데이터 소스 |
| 조건 관리 | `ConditionItem`, `ConditionItemGroup`, `ConditionItemGroupItemMapping`, `ConditionItemGroupItemMapping_Popup`, `ConditionInput_Popup`, `OptionsInput_Popup` | 동적 검색 조건 생성기와 팝업 입력 옵션 보존 |
| 코드/사전/메시지 | `Code`, `CodeClass`, `Dictionary`, `DictionaryClass`, `Message`, `MessageClass` | 다국어/메시지/코드 캐시의 관리 화면 |
| 서비스/쿼리 | `Service`, `ServiceEquipmentMapping`, `Query` | API Service ID, 설비 매핑, Query ID 관리 |
| 배포/스케줄 | `DeployFileUpload`, `DeployFileListPopup`, `DeployHistoryListPopup`, `TaskScheduler`, `QuartzCronGenerator` | 파일 배포 이력, Quartz Cron 생성, Scheduler 작업 관리 |
| 툴바/설정 | `Toolbar`, `Config` | 공통 툴바 버튼, 시스템 설정 조회/저장 |

#### 14.2.6 QMS 설계 보완

참조 C# 클라이언트에는 별도 `Micube.SmartEES.Qms` 프로젝트가 없으나 Java 백엔드와 Query XML에는 QMS 도메인이 존재한다. 따라서 C# 전환 범위에는 API/서비스와 향후 UI 마이그레이션 기준을 모두 포함한다.

| QMS 영역 | 근거 모듈/쿼리 | C# 이전 설계 |
|------|------|------|
| 4M 변경 | `QMS_4M.xml`, `s-rule-qms.chg` | 4M 변경 이력/승인/대상 관리 API와 화면 후보 정의 |
| Claim/NCR | `QMS_CLM.xml`, `QMS_REP.xml`, `QmsTbClaim`, `QmsTbClaimResult`, `QmsTbNcrIssue`, `QmsTbNcrAction` | Claim 등록/처리, NCR 발행/조치, Report API |
| 수입/출하/공정 검사 | `QMS_INP.xml`, `QMS_QCA.xml`, `QmsTbInspDef`, `QmsTbInspResult`, `QmsTbInspDefect` | 검사 정의, 검사 결과, 불량, Lot 검사 API |
| 장기/계측 | `QMS_INSP_LONGTERM.xml`, `QMS_MEASURE.xml`, `QmsTbLongtermReq`, `QmsTbMeasureEquipment`, `QmsTbCalibration` | 장기 검사 요청, 계측기/검교정 관리 API |
| SPC | `QMS_SPC.xml`, `s-rule-qms.spc`, `QmsTbSpcResult`, `QmsTbSpcRuleDef` | SPC Rule, 측정값 분포, 검사 결과 Chart API |
| 공급사 평가 | `QMS_SPM.xml`, `QmsTbSupEvl*` | 공급사 평가 기준/결과 관리 API |
| 기준 정보 | `QMS_STD.xml`, `s-rule-qms.std` | QMS 기준 코드/검사 기준 관리 API |

QMS DB 설계는 현재 MSSQL Factory Schema에 `QCA_TB_*` 위주로 존재하고 Java SO에는 `QMS_TB_*` 계열이 다수 존재한다. C# 전환 전 `QMS_TB_*` 물리 테이블의 DBMS별 스키마 존재 여부를 확정하고, 없으면 SO/Query 기준으로 Migration Script를 추가 작성한다.

### 14.3 SmartControls 개별 컨트롤 보완 설계

`Micube.Framework.SmartControls`의 실제 컨트롤은 단순 UI 래퍼가 아니라 다국어, 권한, Excel, Popup, 조건, Validation, Wait Dialog와 결합되어 있다. 다음 컨트롤별 이전 기준을 적용한다.

| 컨트롤/영역 | 현재 책임 | C# 이전 기준 |
|------|------|------|
| `SmartBandedGrid`, `SmartGridControl`, `SmartBandedGridView` | Grid 컬럼/밴드, Check Row, Validation, Export, 메뉴, Row State 관리 | DevExpress GridControl 기반 유지, `_STATE_`, CheckedRows, Required Column, BestFit, Export API를 호환 구현 |
| `SmartChart` | `ChartControl` 확장, 다국어, Series/Axis/Legend 설정, Zoom/Scroll, Point 선택, 이미지 저장 | `ISupportMultiLanguage` 유지, Ctrl Wheel Zoom/Shift Wheel Scroll, PNG/BMP/GIF/JPEG 저장, Chart Context Menu 구현 |
| FDC `SmartAdvancedChart`, `FdcChart`, `FdcMenuChart` | FDC Trace/Spec Overlay, Chart 메뉴, Panel 배치 | FDC 전용 Chart Adapter로 이전하고 일반 Chart와 API를 분리 |
| `SmartPivotGridControl` | Pivot Row/Column/Data/Filter/Summary Field, 다국어 Caption, Checkbox Field, Grand Total Caption | `PivotGridControl` 기반 유지, `AddRowField`, `AddColumnField`, `AddDataField`, `AddSummaryField`, `CheckedVRows`, `CheckedARows` 구현 |
| `SmartTreeList` | 트리형 설비/메뉴/권한 데이터 표시 | Node Key/Parent Key, Check Node, 다국어 Caption, Plant 필터와 Lazy Load 지원 |
| `SmartSelectPopupEdit`, `RepositoryItemSmartSelectPopupEdit` | 팝업 선택형 에디터, 단건/다건 선택 | `ISmartCustomPopup` 계약, ValueMember/DisplayMember, Search Condition 전달 구현 |
| `SmartComboBox`, `SmartCheckedComboBox`, `SmartGridComboBox`, `SmartSearchLookupEdit` | 코드/쿼리 기반 선택 컨트롤 | CodeClass/Query ID 바인딩, 다국어명 표시, 빈 값/전체 옵션 처리 |
| `SmartDateRangeEdit`, `SmartPeriodEdit` | 기간 조건 입력 | Plant 업무 시작 시간, 기본 기간, 필수/범위 Validation 반영 |
| `SmartLayoutControl`, `SmartTabControl`, `SmartSplitTableLayoutPanel`, `SmartSpliterContainer` | 화면 레이아웃/탭/분할 | 기존 화면의 Dock/Anchor/Tab Order 보존, 화면 해상도별 최소 크기 검증 |
| `SmartPropertyGrid`, `SmartDiagramControl`, `SmartSpreadSheet` | 속성 편집, Diagram, Spreadsheet | 사용 화면 확인 후 기능 동일성 테스트 케이스 작성 |
| Editor 계열 | `SmartTextBox`, `SmartButton`, `SmartButtonEdit`, `SmartCheckEdit`, `SmartMemoEdit`, `SmartRichTextBox` 등 | 기존 DevExpress Editor 옵션, 필수 표시, MaxLength, ReadOnly, 다국어 Caption 유지 |
| Dialog 계열 | `DialogManager`, `SmartMessageBox`/`MSGBox`, `WaitDialog`, `SmartSplash`, `CaptureForm` | Wait Overlay 생명주기, Splash, Error Message Box, Capture 기능을 공통 서비스로 이전 |
| Excel Dialog | `ExcelImportDialog`, 내부 Wizard Page | 14.8의 Import Wizard 설계에 따라 Grid Import와 연동 |

공통 구현 규칙은 다음과 같다.

| 항목 | 설계 |
|------|------|
| 다국어 | `ISupportMultiLanguage` 구현 컨트롤은 `Language.ChangeLanguage` 이벤트를 구독하고 `Language.GetDictionary`, `Language.GetMessage` 결과로 Caption 갱신 |
| 권한 | 메뉴/툴바 권한에 따라 버튼 표시/활성 상태를 적용하고, 저장/삭제/Excel 권한을 Grid Context Menu까지 전파 |
| Validation | Required, MaxLength, DataType, Range, Duplicate Key 검증을 컨트롤별 Error Provider와 저장 전 Rule Validation 양쪽에서 수행 |
| 상태 관리 | DataTable RowState와 현행 `_STATE_` 값(`added`, `modified`, `deleted`)을 동시에 지원해 기존 Rule 입력 구조를 호환 |
| 비동기 처리 | 조회/저장 중 `DialogManager.ShowWaitArea`에 해당하는 Overlay를 표시하고 예외/취소 시 반드시 닫음 |

### 14.4 팝업 폼 보완 설계

팝업은 `SmartPopupBaseForm`, 일부 `SmartBaseForm`, `ISmartCustomPopup` 기반으로 동작한다. C# 전환 시 다음 공통 계약을 적용한다.

| 계약 항목 | 설계 |
|------|------|
| 입력 | 호출 화면에서 `Dictionary<string, object>` 또는 명시적 DTO로 검색 조건, 선택 모드, 초기값 전달 |
| 출력 | 단건은 `DataRow`/DTO, 다건은 `DataTable`/DTO List로 반환하고 `DialogResult.OK`일 때만 호출 화면에 반영 |
| 권한 | 호출 메뉴의 조회/저장 권한을 팝업에 전파하며 팝업 내부 저장 기능은 별도 권한 체크 |
| 다국어 | Popup Title, Grid Caption, Button Caption은 `LanguageType` 변경 이벤트에 반응 |
| 오류 처리 | 팝업 내부 오류는 `UIErrorService`를 통해 표시하고 호출자에게는 취소/실패 상태를 명확히 반환 |
| 재사용 | Select Popup과 업무 Popup을 분리하고, Select Popup은 `SmartSelectPopupEdit`에서 재사용 가능한 Query 기반 구조로 유지 |

모듈별 팝업 이전 목록은 다음과 같다.

| 모듈 | 팝업 목록 |
|------|------|
| FDC | `ActiveParameterSpec_PopUp`, `ConditionFormulaPopup`, `FDCDataChartDetailsPopup`, `FDCInterlockHistoryDetailsPopup`, `FDCInterlockParameterDetailsPopup`, `InterestParameterSelectPopup`, `ParameterCopyPopup`, `ParameterCPKChartPopup`, `ParameterCPKPopup`, `ParameterCPKPopup_samhwa`, `ParameterListPopup`, `ParameterSelectPopup`, `ParameterSpecCopyPopup`, `SPCRulePopup`, `SummaryAutoRegistPopup`, `SummaryParameterListPopup`, `SummaryParameterSelectPopup`, `XScalePopup`, `YScalePopup`, `SPCRuleList`, `SPCSpecPopup` |
| RMS | `ApprovalPathPopup`, `ApprovalProcessingPopup`, `ApprovalRequestCopyPopup`, `ApprovalRequestProductTypePopup`, `ApprovalRequestSpecMappingPopup`, `ApprovalRequestUploadPopup`, `EquipmentProcessRecipeChangeDetailPopup`, `EquipmentSequenceRecipeChangeDetailPopup`, `ProcessRecipeComparePopup`, `ProcessRecipeCopyPopup`, `ProcessRecipeDetailPopup`, `ProcessRecipeEditPopup`, `ProcessRecipeSpecCopyPopup`, `ProcessRecipeUploadPopup`, `RecipeChangeHistoryProcessDetailPopup`, `RecipeChangeHistorySequenceDetailPopup`, `RecipeParameterCopyPopup`, `RecipeRegistrationPopup`, `RecipeValidationHistoryProcessDetailPopup`, `RecipeValidationHistorySequenceDetailPopup`, `SequenceRecipeComparePopup`, `SequenceRecipeDetailPopup`, `SequenceRecipeEditPopup`, `SequenceRecipeUploadPopup` |
| EPT | `EquipmentAlarmHistoryPopup`, `EquipmentAvailabilityHistoryPopup`, `EquipmentLossAlarmHistory_Popup`, `EquipmentStateChangePopup`, `EquipmentStateUpdatePopup`, `FactoryMonitoringEquipmentPopup`, `IndexFormulaTextPopup`, `InterestedSelectEqp_Popup`, `InterestedSelectIndexPopup`, `MachineCycleChartValidationHistory_Popup`, `MccCopyPopup`, `OverallEquipmentEffectiveness_Popup` |
| MDM | `EquipmentPopup`, `EquipmentAlarmPopUp`, `CopyPopup`, `EquipmentTreeListPopup` |
| SystemManagement | `ConditionItemGroupItemMapping_Popup`, `ConditionInput_Popup`, `DeployFileListPopup`, `DeployHistoryListPopup`, `EquipmentPopup`, `OptionsInput_Popup`, `UserClassUserMapping_Popup` |

### 14.5 백엔드 Rule/Communication 서비스 매핑 보완

Java Rule과 Communication Handler는 C#에서 Application Service, Rule Handler, Background Worker로 나누어 이전한다. API Controller는 화면 친화 DTO를 받고, 내부에서는 기존 Rule ID와 Query ID를 보존한 `RuleRegistry`/`QueryRegistry`를 통해 호출한다.

#### 14.5.1 설비 알람/EPT 매핑

| Java 근거 | 주요 Rule/Message | C# 대상 서비스 | 주요 테이블 |
|------|------|------|------|
| `s-rule-ees.ept` | `ExecuteFormula`, `SaveEquipmentProperty`, `SaveIndex`, `SaveInterestIndex`, `SaveLayout`, `SaveLayoutData`, `SearchStateColorPivot` | `EptIndexService`, `EquipmentEptPropertyService`, `FactoryLayoutService`, `EquipmentStateService` | `EPT_TB_INDEX`, `EPT_TB_INTEREST_INDEX`, `EPT_TB_LAYOUT`, `EPT_TB_LAYOUT_EQUIPMENT`, `EPT_TB_EQUIPMENT_EPT_PROPERTY`, `EPT_TB_STATE` |
| `s-communication-ees.ept` | `REQUEST_ALARM_INTERLOCK`, `REPLY_ALARM_INTERLOCK`, `REPORT_ALARM_STATE`, `REQUEST_EQUIPMENT_STATE`, `REPLY_EQUIPMENT_STATE`, `REPORT_EQUIPMENT_STATE`, `REQUEST_STATE_MATRIX`, `REPLY_STATE_MATRIX`, `REQUEST_MCC_INTERLOCK`, `REPLY_MCC_INTERLOCK` | `EquipmentAlarmCommunicationHandler`, `EquipmentStateCommunicationHandler`, `EquipmentInterlockService`, `MccInterlockService` | `EPT_TB_ALARM`, `EPT_TB_ALARM_ACTION`, `EPT_TB_EQUIPMENT_STATUS`, `EPT_TB_STATE_SUMMARY`, `EPT_TB_MCC_ACTION`, `EPT_TB_MCC_EVENT`, `EPT_TB_MCC_SPEC` |
| `s-rule-ees.taskscheduler` | `TaskIndexSummaryDay`, `TaskIndexSummaryDayForCloud`, `InsertDataByEquipment`, `TaskInsertData` | `EptSummaryHostedService`, `EquipmentStatusAggregationJob` | `EPT_TB_INDEX_SUMMARY_DAY`, `EPT_TB_EVENT_SUMMARY`, `EPT_TB_STATE_SUMMARY` |

#### 14.5.2 FDC 매핑

| Java 근거 | 주요 Rule/Message | C# 대상 서비스 | 주요 테이블 |
|------|------|------|------|
| `s-rule-ees.fdc` | `SaveFdcParameter`, `SaveFdcParameterCopy`, `SaveActiveParameterSpec`, `SaveIdleParameterSpec`, `SaveSummaryParameter`, `SaveSummaryParameterSpec`, `SaveVirtualEventParameter`, `SaveParameterStateCondition` | `FdcParameterService`, `FdcSpecService`, `FdcSummaryService`, `FdcVirtualParameterService`, `FdcConditionService` | `FDC_TB_PARAMETER`, `FDC_TB_ACTIVE_PARAMETER_SPEC`, `FDC_TB_IDLE_PARAMETER_SPEC`, `FDC_TB_SUMMARY_PARAMETER`, `FDC_TB_SUMMARY_PARAMETER_SPEC`, `FDC_TB_VIRTUAL_EVENT_PARAMETER`, `FDC_TB_PARAMETER_STATE_CONDITION` |
| `s-rule-ees.fdc` | `SaveTraceGroup`, `SaveTraceGroupParameterMap`, `SaveInterestGroup`, `SaveInterestGroupParameterMap`, `SaveSummaryParameterAutoRegist`, `ExecuteFdcFormula`, `SelectFdcParameterChart` | `FdcTraceGroupService`, `FdcInterestGroupService`, `FdcFormulaService`, `FdcChartQueryService` | `FDC_TB_TRACE_GROUP`, `FDC_TB_TRACE_GROUP_PARAMETER_MAP`, `FDC_TB_INTEREST_GROUP`, `FDC_TB_INTEREST_GROUP_PARAMETER_MAP`, `FDC_TB_TRACE_DATA`, `FDC_TB_EVENT_DATA`, `FDC_TB_SUMMARY_DATA` |
| `s-communication-ees.fdc` | `GEN_FDC_DATA`, `REPORT_FDC_PARAMETER`, `REPORT_FDC_SPEC_CHECK`, `REPORT_FDC_SUMMARY_PARAMETER`, `REQUEST_FDC_INTERLOCK`, `REPLY_FDC_INTERLOCK`, `REQUEST_GET_FDC_PARAMETER`, `REQUEST_SET_FDC_PARAMETER`, `REPLY_SET_FDC_PARAMETER` | `FdcDataIngestionHandler`, `FdcSpecCheckService`, `FdcInterlockService`, `FdcParameterCommunicationHandler` | `FDC_TB_TRACE_DATA`, `FDC_TB_TRACE_SPEC_DATA`, `FDC_TB_EVENT_SPEC_DATA`, `FDC_TB_SUMMARY_SPEC_DATA`, `FDC_TB_INTERLOCK_HIST` |
| `s-rule-ees.taskscheduler` | `TaskBulkFdcData`, `TaskTimeLotFdcData`, `NullInterlockCheck`, `ParameterNullInterlockCheck` | `FdcBulkDataJob`, `FdcLotDataJob`, `FdcNullInterlockCheckJob` | FDC Trace/Event/Summary Data 및 Interlock Hist |

#### 14.5.3 RMS/Recipe 매핑

| Java 근거 | 주요 Rule/Message | C# 대상 서비스 | 주요 테이블 |
|------|------|------|------|
| `s-rule-ees.rms` | `SaveProcessRecipe`, `SaveSequenceRecipe`, `SaveRecipeParameter`, `SaveRecipeMapping`, `SaveProcessRecipeCopy`, `SaveRecipeParameterCopy` | `RmsProcessRecipeService`, `RmsSequenceRecipeService`, `RmsRecipeParameterService`, `RmsRecipeMappingService` | `RMS_TB_PROCESS_RECIPE`, `RMS_TB_SEQUENCE_RECIPE`, `RMS_TB_SEQUENCE_RECIPE_MAP`, `RMS_TB_RECIPE_PARAMETER`, `RMS_TB_ITEM_SEGMENT_RECIPE_MAP` |
| `s-rule-ees.rms` | `RegistRecipeInformation`, `SaveEquipmentRecipeViewUpload`, `SaveEquipmentRmsProperty`, `SaveCopyRecipeSpecPopup`, `SaveEditRecipeSpecPopup`, `SaveEditSequenceRecipeMapPopup` | `RmsRecipeRegistrationService`, `EquipmentRecipeService`, `EquipmentRmsPropertyService`, `RecipeSpecService` | `RMS_TB_EQUIPMENT_RECIPE_HIST`, `RMS_TB_EQUIPMENT_RECIPE_PARAMETER_HIST`, `RMS_TB_EQUIPMENT_RECIPE_SEQUENCE_HIST`, `RMS_TB_EQUIPMENT_RMS_PROPERTY`, Spec Hist |
| `s-communication-ees.rms` | `REQUEST_DOWNLOAD_RECIPE`, `REPLY_DOWNLOAD_RECIPE`, `REQUEST_UPLOAD_RECIPE`, `REPLY_UPLOAD_RECIPE`, `REQUEST_DOWNLOAD_SEQUENCE_RECIPE`, `REPLY_DOWNLOAD_SEQUENCE_RECIPE`, `REQUEST_UPLOAD_SEQUENCE_RECIPE`, `REPLY_UPLOAD_SEQUENCE_RECIPE` | `RecipeDownloadHandler`, `RecipeUploadHandler`, `SequenceRecipeTransferHandler` | `RMS_TB_RECIPE_DOWNLOAD_HIST`, `RMS_TB_RECIPE_VALIDATION_HIST`, 장비 레시피 Hist |
| `s-communication-ees.rms` | `REQUEST_CHANGE_RECIPE`, `REPLY_CHANGE_RECIPE`, `REQUEST_CREATE_RECIPE`, `REPLY_CREATE_RECIPE`, `REQUEST_DELETE_RECIPE`, `REPLY_DELETE_RECIPE`, `REQUEST_RECIPE_LIST`, `REPLY_RECIPE_LIST`, `REQUEST_RECIPE_MODE`, `REQUEST_WORK_ORDER`, `REPORT_WORK_ORDER` | `RecipeChangeHandler`, `RecipeCatalogHandler`, `RecipeModeHandler`, `WorkOrderRecipeHandler` | RMS Recipe/Mapping/Hist 테이블 |

#### 14.5.4 승인/Scheduler 공통 매핑

| Java 근거 | 주요 Rule | C# 대상 서비스 | 설계 |
|------|------|------|------|
| `s-rule-ees.approval` | `RequestApproval`, `ChangeApprovalState`, `SaveApprovalFormat`, `SaveApprovalPath`, `SaveMyApprovalPath` | `ApprovalWorkflowService`, `ApprovalPathService` | RMS Recipe Approval, 향후 QMS Approval과 공통 사용 |
| `s-rule-ees.taskscheduler` | `TASK_RECIPENOTMAPPING_CHECK`, EPT/FDC 집계 작업 | `QuartzHostedService`, `TaskSchedulerService` | SystemManagement `TaskScheduler` 화면과 Quartz Cron Generator 연계 |
| SystemManagement `Service`, `Query` | Service/Query 메타데이터 | `ServiceMetadataService`, `QueryMetadataService` | Rule/Query ID를 하드코딩하지 않고 메타데이터 기반으로 로드 |

### 14.6 다국어/다중 플랜트 처리 상세 설계

현행 클라이언트는 로그인 시 `LanguageType`, `PlantId`, `PlantStartBusinessHour`를 설정하고, 메시지 전송 시 `NetworkSettings.Default.MessageSettings`에 `User`, `Uiid`, `LanguageType`을 주입한다. C# 전환에서는 이 값을 API, Rule, Query, UI 전 계층에 명시적으로 전파한다.

| 영역 | 설계 |
|------|------|
| 로그인 | `GetLanguageTypeList`, `GetPlantListOnLogin` 결과를 기반으로 언어/Plant 선택 UI를 제공하고 선택값을 `SettingConfig`에 저장 |
| 사용자 컨텍스트 | `UserContext`에 `UserId`, `Uiid`, `LanguageType`, `EnterpriseId`, `PlantId`, `AreaId`, `PlantStartBusinessHour`, `UserIp`, `ConnectionKey`를 포함 |
| API 전파 | 모든 API 요청 Header 또는 Body Metadata에 `LanguageType`, `PlantId`, `Uiid`, `CorrelationId`를 포함하고 서버에서 `RuleContext`로 변환 |
| Query 전파 | 기존 `_TXNINFO.LANGUAGETYPE`와 동일하게 Query Parameter를 제공하고 Plant Scope 테이블에는 `PLANT_ID` 조건을 강제 |
| Plant 권한 | `COM_TB_USER_PLANT_MAP` 기준으로 사용자 접근 가능 Plant를 조회하고, 서버에서 권한 없는 Plant 요청을 차단 |
| Plant 명칭 | `STD_TB_PLANT`의 `PLANT_NAME_KO_KR`, `PLANT_NAME_EN_US`, `PLANT_NAME_ZH_CN`, `PLANT_NAME_VI_VN`, `PLANT_NAME_LO_LO`를 `LanguageType`에 따라 반환 |
| 업무 일자 | `START_BUSINESS_HOUR`를 기준으로 조회 기본 기간과 집계 기준일을 계산 |
| UI 다국어 | `Language.Dictionary`, `Language.Message`, `Language.LanguageTypes`를 C# 캐시로 이전하고 `LanguageChangedEventArgs`와 동일한 이벤트로 Control Caption 갱신 |
| Fallback | 언어별 Dictionary/Message가 없으면 `lo-LO` 또는 기본 언어로 Fallback하고, 누락 키를 로그로 남김 |

Plant 필터는 화면 검색 조건에만 의존하지 않는다. 저장/삭제 API에서 서버가 `RuleContext.PlantId`와 입력 DTO의 `PlantId`를 검증해 Cross-Plant 갱신을 방지한다.

### 14.7 에러 처리 및 예외 전파 상세 설계

현행 `MessageWorker.Execute`는 서버 응답 `Success`가 실패이면 `GetFailMessage()`로 예외를 발생시키고, UI는 `UIHelper.ShowError`에서 Wait Dialog를 닫은 뒤 `MSGBox.Show(MessageBoxType.Error, ...)`로 표시한다. 이 흐름을 다음 표준 계약으로 이전한다.

| 계층 | 설계 |
|------|------|
| API 응답 | 성공은 `code = "0"`, 실패는 `code`, `errorCode`, `messageId`, `message`, `action`, `correlationId`, `details`를 포함한 `RuleResponse` 또는 `ProblemDetails`로 반환 |
| 서버 예외 | Validation 오류, 업무 Rule 오류, 외부 통신 오류, DB 오류, 인증/권한 오류를 별도 Exception Type으로 분리 |
| Java 호환 | 기존 `EESException` 문자열 제거 로직은 서버에서 정규화하고, 클라이언트에는 사용자 메시지만 전달 |
| 클라이언트 예외 | `MessageException`, `SilentException`, `RuleExecutionException`, `CommunicationException`으로 매핑 |
| UI 표시 | 예외 발생 시 모든 Wait Overlay/Splash를 닫고, 업무 오류는 다국어 메시지, 시스템 오류는 추적 ID와 일반 메시지를 표시 |
| 로깅 | 서버는 `CorrelationId`, `UserId`, `PlantId`, `MenuId`, `RuleId`, `QueryId`, `TxnHistKey`를 구조화 로그로 기록 |
| 재시도 | 설비 통신, Kafka/SignalR, 파일 업로드와 같은 외부 연동 오류는 재시도 가능 여부를 `action`에 포함 |
| Validation | Grid Row 단위 오류는 전체 저장 실패 메시지와 함께 Row/Column 오류 목록을 반환해 UI에서 셀 오류로 표시 |

저장 API는 부분 성공을 기본 허용하지 않는다. 업무상 부분 성공이 필요한 경우 응답에 `succeededRows`, `failedRows`를 포함하고 화면에서 사용자가 재처리할 수 있도록 한다.

### 14.8 Excel 가져오기/내보내기 상세 설계

현행 `ExcelImportDialog.ShowDialog(ExcelImportInfo)`는 파일 선택, 옵션, 컬럼 매핑, 미리보기 Wizard를 통해 `DataTable`을 반환한다. Grid Export는 `ExportToXlsx`와 `XlsxExportOptions`/`XlsxExportOptionsEx`를 사용한다.

#### 14.8.1 Excel Import

| 단계 | 설계 |
|------|------|
| 파일 선택 | `.xlsx`, `.xls` 파일을 선택하고 파일 잠금/확장자/크기 제한을 검증 |
| Sheet 선택 | Workbook의 Sheet 목록을 읽어 `SelectedSheet`를 지정 |
| Header 옵션 | `UseHeaderRow`, `HeaderRowIndex`를 지원하고 Header가 없는 경우 기본 컬럼명 생성 |
| 컬럼 매핑 | `BaseColumns`, `KeyColumns`, `NotAllowNullColumns`, `ImportColumns` 기준으로 Excel Column과 Grid Column을 매핑 |
| 미리보기 | 데이터 타입 변환, 필수값, Key 중복, MaxLength 오류를 미리보기 Grid에 표시 |
| 반영 | 정상 Row만 대상 Grid에 추가하거나 전체 실패 정책을 선택할 수 있게 하고 `_STATE_ = "added"`를 부여 |
| 감사 | Import 파일명, 사용자, 메뉴, Row Count, 실패 Count를 로그에 남김 |

#### 14.8.2 Excel Export

| 대상 | 설계 |
|------|------|
| Grid | 표시 컬럼/필터/정렬/밴드 구조를 유지해 `.xlsx`로 Export |
| 데이터 타입 | 현행 `TextExportMode.Text`가 필요한 화면은 숫자/코드 앞자리 0 손실 방지를 위해 Text Mode 유지 |
| 대용량 | 대량 Export는 서버 Streaming 또는 Background Export로 전환하고 UI에는 진행률/취소 제공 |
| 권한 | 메뉴/툴바 권한에 `ExcelExport` 권한을 포함하고 권한 없는 사용자는 Context Menu에서도 비활성화 |
| Chart | `SmartChart`의 PNG/BMP/GIF/JPEG 저장 기능을 유지하고 FDC Chart 상세 팝업에서도 동일하게 제공 |
| 보안 | 개인정보/품질 민감 데이터 화면은 Export 로그와 사유 입력 옵션을 제공 |

### 14.9 설정 관리 보완 설계

현행 설정은 로컬 JSON과 서버 저장을 혼합한다. C# 전환 시 파일 경로와 데이터 구조를 호환하되, 스키마 버전과 손상 파일 복구를 추가한다.

| 설정 | 현행 근거 | C# 이전 설계 |
|------|------|------|
| 로그인 설정 | `%AppData%/Micube/SmartEES/Setting/LoginSetting.json`, `SettingConfig.IsSaveLoginId`, `SaveLoginId`, `LanguageType`, `PlantId` | 동일 경로 또는 신규 경로에 `schemaVersion`을 추가하고 기존 JSON 자동 Migration 수행 |
| 최근 메뉴 | `RecentMenuSetting_{userId}.json`, `SettingConfig.RecentMenu` | 사용자별 최근 메뉴 목록, 최대 개수, 중복 제거, 메뉴 권한 변경 시 정리 |
| 조건 저장 | `ConditionSetting_{userId}_{menuId}.json`, 날짜별 조건 Dictionary, `GetSaveConditionCount()` 제한 | 메뉴별 저장 조건, 조건 그룹/항목 ID 기준 저장, 오래된 조건 자동 삭제, 손상 파일 백업 |
| 즐겨찾기 | `SaveFavoriteMenu` Rule, `USERID`, `UIID`, `MENUID`, `REGTYPE = "Favorite"`, `DISPLAYSEQUENCE` | 서버 저장 유지, 메뉴 권한 변경 시 숨김 처리, 순서 변경 API 추가 |
| 언어/Plant | `SettingConfig.LanguageType`, `SettingConfig.PlantId` | 로그인 기본값으로 사용하되 사용자 권한에서 제거된 Plant이면 재선택 요구 |

설정 저장은 화면 종료 시점에만 몰아서 저장하지 않고 변경 즉시 Debounce 저장한다. 다중 실행 충돌을 줄이기 위해 사용자별 파일 Lock 또는 임시 파일 저장 후 Atomic Replace를 적용한다.

### 14.10 모바일 알림 설계

`SYS_TB_MOBILE_NOTIFICATION`과 `SYS_TB_MOBILE_NOTIFICATION_USERS`는 PostgreSQL/Oracle Schema에 존재하지만 MSSQL Factory Schema에서는 확인되지 않는다. C# 전환 시 DBMS별 스키마 정합성을 먼저 맞춘 뒤 다음 서비스를 구현한다.

| 테이블 | 주요 컬럼 | 설계 |
|------|------|------|
| `SYS_TB_MOBILE_NOTIFICATION` | `NOTIFICATION_ID`, `TITLE`, `CONTENT`, `RESULT_TYPE`, `RESULT_MESSAGE`, 감사 컬럼, `VALID_STATE` | 알림 요청 단위 Header. 업무 서비스가 알림 생성 시 제목/내용/결과를 기록 |
| `SYS_TB_MOBILE_NOTIFICATION_USERS` | `NOTIFICATION_ID`, `NOTIFICATION_SEQ`, `USER_ID`, `MOBILE_ID`, `TOKEN`, `STATUS`, 감사 컬럼, `VALID_STATE` | 사용자/단말별 전송 대상과 전송 상태 관리 |

| 기능 | 설계 |
|------|------|
| 생성 | 업무 Rule 또는 Scheduler가 `IMobileNotificationService.CreateAsync`를 호출해 Header와 User Row를 생성 |
| 대상 산정 | 사용자, UserClass, Mailing Group, 설비 담당자, 알람 수신자 매핑을 대상 산정 소스로 사용 |
| 전송 | Push Provider Adapter를 분리하고 토큰별 성공/실패를 `STATUS`, `RESULT_TYPE`, `RESULT_MESSAGE`에 반영 |
| 재시도 | 네트워크/Provider 일시 오류는 재시도 큐로 이동하고 영구 실패 토큰은 비활성 후보로 기록 |
| 조회 | SystemManagement 또는 운영 화면에서 알림 Header/대상/결과를 조회할 수 있는 API 제공 |
| 보안 | 알림 본문에 민감 정보가 포함되지 않도록 업무별 Template ID와 변수 치환 방식을 권장 |

### 14.11 QMS 및 품질 백엔드 보완 상세

QMS는 Java SO/Rule/Query의 범위가 크므로 UI가 아직 C# 프로젝트에 없더라도 백엔드 이전 범위에서 제외하면 안 된다. 다음 기준으로 API를 구성한다.

| 서비스 | 담당 기능 | 근거 |
|------|------|------|
| `QmsInspectionService` | 검사 정의, 검사 항목, 검사 결과, 검사 불량, Lot 검사 조회/저장 | `QmsTbInspDef*`, `QmsTbInspItem*`, `QmsTbInspResult*`, `QMS_INP.xml`, `QMS_QCA.xml` |
| `QmsSpcService` | SPC Rule, SPC 결과, 측정값 분포, Chart 조회 | `QmsTbSpcResult`, `QmsTbSpcRuleDef`, `QMS_SPC.xml`, `s-rule-qms.spc` |
| `QmsClaimService` | Claim 접수, 처리 결과, Report | `QmsTbClaim`, `QmsTbClaimResult`, `QMS_CLM.xml` |
| `QmsNcrService` | NCR 발행, 조치, Report | `QmsTbNcrIssue`, `QmsTbNcrAction`, `QMS_REP.xml` |
| `QmsMeasureService` | 계측기, 검교정, 측정 관리 | `QMS_MEASURE.xml`, `s-rule-qms.meq` |
| `QmsLongTermInspectionService` | 장기 검사 요청/결과 | `QMS_INSP_LONGTERM.xml`, `s-rule-qms.ltm` |
| `QmsChangeService` | 4M/변경 관리 | `QMS_4M.xml`, `s-rule-qms.chg` |
| `QmsSupplierEvaluationService` | 공급사 평가 기준/결과 | `QMS_SPM.xml`, `s-rule-qms.spm` |

QMS API는 `qms` Query Namespace를 보존하고, 향후 UI가 추가될 때 동일 DTO를 사용하도록 화면 독립적인 Application Service로 작성한다. DB Migration은 Java SO Primary Key 정의, Query XML Join, 실제 운영 DB 스키마를 함께 검증한 뒤 확정한다.

### 14.12 기타 중요 보완 항목

| 항목 | 보완 설계 |
|------|------|
| Scheduler | SystemManagement `TaskScheduler` 화면과 `s-rule-ees.taskscheduler` 작업을 Quartz.NET 기반 `IHostedService`로 이전하고, Cron 생성/검증 UI를 유지 |
| 승인 워크플로우 | RMS뿐 아니라 QMS/변경관리에서도 재사용 가능하도록 Approval Path, Approval Format, My Approval Path를 공통 모듈로 분리 |
| 실시간 데이터 | FDC RealTime 수집, Equipment State/Alarm Report, Recipe 통신 Reply는 SignalR/Kafka Adapter로 분리하고 화면 구독 해제 시 리소스를 정리 |
| 파일/첨부 | SystemManagement 배포 파일, QMS 첨부 파일, Excel Import 파일은 저장소 정책, 파일 크기 제한, 바이러스 검사 Hook, 다운로드 권한을 정의 |
| DBMS별 Query | `Config/Query/xml`의 MSSQL/Oracle/PostgreSQL/MySQL/SQLite별 Query 차이를 `IQueryProvider`로 캡슐화하고 Query ID 회귀 테스트를 작성 |
| 감사/이력 컬럼 | `TXN_HIST_KEY`, `LAST_TXN_*`, `VALID_STATE`, Hist 테이블 저장 규칙을 ServiceObjectProcessor에서 공통 처리 |
| 메뉴/툴바 권한 | 화면 로딩, 팝업, Context Menu, Excel, 저장/삭제 버튼까지 동일한 권한 모델을 적용 |
| 배포 파일 | `DeployFileUpload`, 배포 이력 팝업을 통해 클라이언트 파일 배포/버전 관리 흐름을 유지 |
| 테스트 기준 | 화면별 조회/저장/삭제/Excel/권한/다국어/Plant 필터/팝업 반환/예외 처리 테스트 케이스를 Phase별 완료 조건으로 정의 |

---

## 15. 캐싱 전략 설계

현행 SmartUX3.5는 로그인과 화면 초기화 시 공통 기준 데이터를 반복 조회한다. `Language.Dictionary`, `Language.Message`, `Language.LanguageTypes`는 C# 클라이언트의 정적 Store에 적재되고, 메뉴는 `MenuRepository.InitMenu()`에서 `GetMenuList` Query 결과를 `DataTable`로 보관한다. 코드 콤보는 여러 화면에서 `GetCodeList`, `CODE_*_COMBO`, `SelectSYSCode` 계열 Query를 직접 호출한다. C# 전환 후에는 서버에서 `IMemoryCache` 기반 공통 캐시를 제공하고, 클라이언트는 로그인 세션 캐시와 서버 응답 캐시를 함께 사용한다.

### 15.1 적용 대상

| 대상 | 현행 조회 패턴 | 캐시 단위 | TTL |
|------|------|------|------|
| `Language.Dictionary` | `GetDictionaryList`가 `SYS_TB_DICTIONARY`, `SYS_TB_CONDITION_ITEM`, `SYS_TB_TOOLBAR`, `SYS_TB_MENU`를 Union 조회하고 `LANGUAGETYPE`별 명칭 반환 | `PlantId + LanguageType + ServiceId`, 개별 키는 `ServiceId + DictionaryId + LanguageType` | 로그인 세션 동안 유지 |
| `Language.Message` | `GetMessageList`가 `SYS_TB_MESSAGE`를 조회하고 `MESSAGE_ID`, `TITLE`, `MESSAGE`를 언어별 반환 | `PlantId + LanguageType + ServiceId`, 개별 키는 `ServiceId + MessageId + LanguageType` | 로그인 세션 동안 유지 |
| `Language.LanguageTypes` | `GetLanguageTypeList`가 `SYS_TB_CONFIG.USE_LANGUAGE_TYPE`와 `SYS_TB_CODE`의 `LanguageType` 코드를 조합 | `PlantId + ApplicationId` 또는 System 공통 | 로그인 세션 동안 유지 |
| 메뉴 트리 | `GetMenuList`가 `SYS_TB_MENU`, `SYS_TB_MENU_AUTHORITY`, `SYS_TB_AUTHORITY_USER`, `SYS_TB_DICTIONARY`를 조인하고 사용자 권한과 `UIID` 기준으로 필터 | `PlantId + LanguageType + UiId + UserId` | 10분 |
| 메뉴 오브젝트/툴바 권한 | `GetMenuObjectList`, `SYS_TB_MENU_ITEM_AUTHORITY`, `SYS_TB_TOOLBAR_AUTHORITY`, `SYS_TB_RULE_AUTHORITY` 조회 | `PlantId + LanguageType + UiId + MenuId + UserId` | 10분 |
| 코드/코드클래스 | `GetCodeList`, `CODE_*_COMBO`, `SelectSYSCodeClass`, `SelectSYSCode`가 `SYS_TB_CODE`, `SYS_TB_CODE_CLASS`, `COM_TB_CODE`, `COM_TB_CODE_CLASS`를 조회 | `PlantId + LanguageType + CodeClassId` | 30분 |
| 권한 목록 | `SYS_TB_AUTHORITY_USER`, `SYS_TB_MENU_AUTHORITY`, `SYS_TB_RULE_AUTHORITY` 조회 | `PlantId + UiId + UserId` 또는 `PlantId + AuthorityId` | 10분 |

`SYS_TB_*` 기준 데이터처럼 Plant 컬럼이 없는 데이터는 `PlantId = "GLOBAL"`로 정규화한다. `COM_TB_CODE`, `COM_TB_CODE_CLASS`처럼 Plant 스코프가 있는 코드는 실제 Plant를 키에 포함한다.

### 15.2 캐시 키 설계

캐시 키는 사람이 추적할 수 있도록 네임스페이스를 포함한다. 기본 형식은 `sux:{category}:{plantId}:{languageType}:{objectId}:{variant}`로 통일한다.

| Category | ObjectId | Variant 예시 |
|------|------|------|
| `lang:dictionary` | `ServiceId` 또는 `DictionaryId` | `all`, `item:{DictionaryId}` |
| `lang:message` | `ServiceId` 또는 `MessageId` | `all`, `item:{MessageId}` |
| `lang:types` | `ApplicationId` | `all` |
| `menu:tree` | `UiId` | `user:{UserId}` |
| `menu:objects` | `UiId:MenuId` | `user:{UserId}` |
| `code:list` | `CodeClassId` | `system`, `standard`, `factory` |
| `code:class` | `CodeClassId` 또는 `parent:{ParentCodeClassId}` | `system`, `standard` |
| `auth:list` | `UiId` 또는 `AuthorityId` | `user:{UserId}`, `menu:{MenuId}` |

키 생성 시 `PlantId`, `LanguageType`, `ObjectId`를 모두 대문자 또는 불변 문자열로 정규화한다. 현행 `Language.LanguageStore`가 `ServiceId + ItemId + LanguageType`를 대문자로 조합하는 방식과 호환되도록, 다국어 개별 조회 키도 대소문자 차이를 허용하지 않는다.

### 15.3 TTL 및 세션 정책

| 구분 | 정책 |
|------|------|
| 사전/메시지 | 로그인 시 `GetDictionaryList`, `GetMessageList`, `GetLanguageTypeList`를 한 번 로드하고 서버 캐시는 절대 만료 없이 변경 이벤트까지 유지한다. 클라이언트 세션 캐시는 로그아웃 또는 언어 변경 시 폐기한다. |
| 메뉴 | 사용자별 권한과 UIID에 따라 달라지므로 10분 절대 만료를 둔다. 메뉴/권한 변경 이벤트가 발생하면 TTL과 무관하게 즉시 제거한다. |
| 코드 | CodeClass 단위로 30분 절대 만료를 둔다. 콤보/조건 항목은 코드 변경이 드물지만 화면 전반에 영향을 주므로 저장 이벤트에서 즉시 제거한다. |
| 권한 | 사용자 권한 변경, 메뉴 권한 변경, Rule 권한 변경 시 즉시 제거하고, 누락 이벤트 대비 10분 TTL을 둔다. |
| Negative Cache | 존재하지 않는 DictionaryId, MessageId, CodeClassId는 1분 이하로만 보관해 신규 등록 직후 미표시를 방지한다. |

### 15.4 이벤트 기반 무효화

SystemManagement 저장 Rule/API의 Commit 이후에 캐시 무효화 이벤트를 발행한다. 트랜잭션이 실패한 경우에는 무효화하지 않는다.

| 저장 흐름 | 현행 근거 | 무효화 대상 |
|------|------|------|
| Dictionary 저장/삭제 | `SaveDictionaryInfo`, `SaveLanguageInfo.insert/update/delete`가 `SysTbDictionary`를 CUD | `lang:dictionary:*`, 관련 `menu:tree:*`, `menu:objects:*` |
| Message 저장/삭제 | `SaveMessage`, `SaveMessageClass` 화면이 `SYS_TB_MESSAGE` 계열 저장 Rule 호출 | `lang:message:*` |
| Code/CodeClass 저장/삭제 | `SaveCodeCodeClassInfo`가 `SysTbCodeClass`, `SysTbCode`를 동일 Batch로 저장하고 CodeClass 삭제 시 하위 Code도 삭제 | `code:list:{CodeClassId}`, `code:class:{CodeClassId}`, `lang:types:*` if `CodeClassId = LanguageType` |
| Menu 저장/삭제 | `SaveMenuInfo`가 `SysTbMenu`, `SysTbMenuAuthority`와 Dictionary를 함께 저장 | `menu:tree:*`, `menu:objects:*`, `auth:list:*`, `lang:dictionary:*` |
| MenuItem/Toolbar/Rule 권한 저장 | `SaveMenuAuthorityData`, `SaveRuleAuthorityData`, `SaveToolbarList`, `SaveMenuItemList` | `menu:objects:*`, `auth:list:*`, 해당 사용자의 `menu:tree:*` |
| 사용자/권한 매핑 저장 | `SYS_TB_AUTHORITY_USER`, UserClass 관련 저장 | `auth:list:*`, 해당 사용자의 `menu:tree:*` |

단일 서버 기본 구성에서는 프로세스 내부 이벤트로 충분하다. 여러 API 인스턴스로 확장하면 DB Commit 이후 `CacheInvalidationEvent`를 Kafka 또는 Redis Pub/Sub로 발행하고, 각 인스턴스가 동일 Prefix를 제거한다.

구현은 본 절을 다음과 같이 적응한다. 캐시 드라이버(MemoryCacheDriver, Redis 드라이버)는 인프라 계층에 존재하지만 현재 어떤 Application 서비스에도 연결되어 있지 않다 — API 서버에 서버측 공유 캐시가 없으므로 위 표의 무효화 이벤트를 발행할 대상 자체가 아직 없고, 본 절의 이벤트 기반 무효화는 적용을 보류한다. Web 계층의 MenuCacheService는 Blazor 서킷(사용자 세션) 스코프의 Task 캐시여서 서킷 종료와 함께 자연 소멸하고 재접속 시 갱신되므로 별도 무효화가 필요 없다. 이후 조회 빈도가 높은 Dictionary/Code/Menu 조회에 서버측 캐시를 도입하는 시점에 본 절의 저장 흐름별 무효화 표를 그대로 적용한다.

### 15.5 CacheService 인터페이스

```csharp
public enum CacheArea
{
    LanguageDictionary,
    LanguageMessage,
    LanguageTypes,
    MenuTree,
    MenuObjects,
    CodeList,
    CodeClass,
    AuthorityList
}

public sealed record CacheKey(
    CacheArea Area,
    string PlantId,
    string LanguageType,
    string ObjectId,
    string? Variant = null)
{
    public override string ToString()
    {
        static string Normalize(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToUpperInvariant();

        var plantId = Normalize(PlantId, "GLOBAL");
        var languageType = Normalize(LanguageType, "ALL");
        var objectId = Normalize(ObjectId, "ALL");
        var variant = Normalize(Variant, "DEFAULT");

        return $"sux:{Area}:{plantId}:{languageType}:{objectId}:{variant}";
    }
}

public sealed record CachePolicy(
    TimeSpan? AbsoluteExpiration,
    bool KeepForLoginSession = false,
    int Size = 1);

public interface ICacheService
{
    Task<T> GetOrCreateAsync<T>(
        CacheKey key,
        Func<CancellationToken, Task<T>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default);

    bool TryGet<T>(CacheKey key, out T? value);

    void Set<T>(CacheKey key, T value, CachePolicy policy);

    void Invalidate(CacheKey key);

    void InvalidateByPrefix(CacheArea area, string? plantId = null, string? objectId = null);

    Task PublishInvalidationAsync(
        CacheArea area,
        string? plantId,
        string? objectId,
        string reason,
        CancellationToken cancellationToken = default);
}

public sealed class MemoryCacheService : ICacheService
{
    private readonly IMemoryCache memoryCache;
    private readonly ILogger<MemoryCacheService> logger;

    public MemoryCacheService(IMemoryCache memoryCache, ILogger<MemoryCacheService> logger)
    {
        this.memoryCache = memoryCache;
        this.logger = logger;
    }

    public async Task<T> GetOrCreateAsync<T>(
        CacheKey key,
        Func<CancellationToken, Task<T>> factory,
        CachePolicy policy,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = key.ToString();
        if (memoryCache.TryGetValue(cacheKey, out T? cached) && cached is not null)
        {
            return cached;
        }

        var value = await factory(cancellationToken).ConfigureAwait(false);
        Set(key, value, policy);
        return value;
    }

    public bool TryGet<T>(CacheKey key, out T? value) =>
        memoryCache.TryGetValue(key.ToString(), out value);

    public void Set<T>(CacheKey key, T value, CachePolicy policy)
    {
        var options = new MemoryCacheEntryOptions
        {
            Size = policy.Size
        };

        if (policy.AbsoluteExpiration is not null)
        {
            options.AbsoluteExpirationRelativeToNow = policy.AbsoluteExpiration;
        }

        memoryCache.Set(key.ToString(), value, options);
    }

    public void Invalidate(CacheKey key) => memoryCache.Remove(key.ToString());

    public void InvalidateByPrefix(CacheArea area, string? plantId = null, string? objectId = null)
    {
        // IMemoryCache는 Prefix 삭제 API가 없으므로 실제 구현은 키 인덱스를 별도 보관한다.
        // Redis 전환 시 SCAN sux:{area}:{plantId}:*:{objectId}:* 또는 Tag 기반 삭제로 대체한다.
        logger.LogInformation(
            "Cache invalidation requested. Area={Area}, PlantId={PlantId}, ObjectId={ObjectId}",
            area,
            plantId,
            objectId);
    }

    public Task PublishInvalidationAsync(
        CacheArea area,
        string? plantId,
        string? objectId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        InvalidateByPrefix(area, plantId, objectId);
        logger.LogInformation(
            "Cache invalidation published. Area={Area}, PlantId={PlantId}, ObjectId={ObjectId}, Reason={Reason}",
            area,
            plantId,
            objectId,
            reason);
        return Task.CompletedTask;
    }
}
```

### 15.6 분산 캐시 확장 경로

1. Phase 1은 단일 서버 기준으로 `IMemoryCache`와 키 인덱스 기반 Prefix 삭제를 구현한다.
2. Scale-out이 필요해지면 `ICacheService` 구현체만 `RedisCacheService`로 교체한다.
3. Redis Key는 동일한 `sux:{category}:{plantId}:{languageType}:{objectId}:{variant}` 형식을 사용한다.
4. 무효화 이벤트는 Redis Pub/Sub 또는 Kafka Topic `sux.cache.invalidate`로 전파한다.
5. 대용량 메뉴/권한 데이터는 Redis Hash로 저장하고, 다국어/코드는 String JSON 또는 MessagePack 직렬화를 사용한다.

---

## 16. CI/CD 파이프라인 설계

마이그레이션 결과물은 ASP.NET Core API, 공통 라이브러리, WinForms 클라이언트 DLL, DB Migration 스크립트, 배포 메타데이터로 나눈다. 파이프라인은 동일한 검증 단계를 거치되 환경별 승인과 배포 방식을 다르게 적용한다.

### 16.1 브랜치 전략

| 브랜치 | 용도 | 보호 규칙 |
|------|------|------|
| `feature/*` | 기능/화면/모듈 단위 개발 | Pull Request 생성 전 로컬 Unit Test 필수 |
| `develop` | 통합 개발 브랜치 | PR 리뷰 1인 이상, build/unit test 통과 |
| `staging` | 운영 반영 전 검증 | develop에서 승격, Integration Test와 DB Migration Dry-run 통과 |
| `main` | 운영 배포 기준 | staging에서만 병합, 수동 승인과 릴리스 태그 필수 |

릴리스 태그는 `v{major}.{minor}.{patch}` 형식으로 생성하고, DB Migration과 클라이언트 버전 파일도 같은 버전을 기록한다.

### 16.2 빌드 파이프라인

| 단계 | 수행 내용 | 산출물 |
|------|------|------|
| Restore | `dotnet restore`, NuGet Feed 인증, 패키지 잠금 검증 | 복원 로그 |
| Build | `dotnet build --configuration Release --no-restore` | API DLL, 공통 DLL, WinForms DLL |
| Unit Test | `dotnet test --configuration Release --no-build --filter Category!=Integration` | TRX, Coverage |
| Integration Test | Testcontainer 또는 전용 DB/Kafka/SignalR 테스트 환경에서 API, Query, Rule Service 검증 | TRX, DB 검증 로그 |
| Static Check | Nullable, Analyzer, Format, 보안 패키지 스캔 | 분석 리포트 |
| Package | API Publish, 클라이언트 DLL Package, Query XML, Migration Script, 배포 Manifest 묶음 | `nexames-{version}.zip` |

Integration Test에는 `GetDictionaryList`, `GetMessageList`, `GetLanguageTypeList`, `GetMenuList`, `GetCodeList`의 주요 Query ID 회귀 테스트를 포함한다. DBMS별 Query Provider를 유지하는 경우 MSSQL을 기본으로 검증하고 Oracle/PostgreSQL은 staging에서 별도 Matrix로 실행한다.

### 16.3 환경별 파이프라인

| 환경 | Trigger | 배포 방식 | 승인 |
|------|------|------|------|
| dev | `develop` 병합 | 자동 배포. API 재시작, 클라이언트 배포 서버에 최신 DLL 업로드 | 없음 |
| staging | `staging` 병합 또는 Release Candidate 태그 | 수동 승인 후 배포. DB Migration Dry-run, Seed 데이터 검증, Smoke Test 실행 | 개발 리드 또는 QA 승인 |
| prod | `main` 태그 | Blue-Green 배포. Green에 API와 클라이언트 패키지 배포 후 헬스체크 통과 시 라우팅 전환 | 운영 승인 |

운영 Blue-Green은 API 서버, 배포 파일 저장소, DB Migration 상태를 분리해 관리한다. DB Schema 변경이 하위 호환되지 않는 경우에는 Expand and Contract 방식으로 먼저 컬럼/테이블을 추가하고, 다음 릴리스에서 구 구조를 제거한다.

### 16.4 DB Migration 자동화

| 항목 | 설계 |
|------|------|
| 스크립트 위치 | `db/migrations/{phase}/{version}_{description}.sql` |
| 버전 테이블 | `SYS_DB_MIGRATION_HISTORY`에 `VERSION`, `PHASE`, `CHECKSUM`, `APPLIED_TIME`, `APPLIED_BY`, `STATUS` 기록 |
| Phase 관리 | Phase별 Schema 변경, Seed, Backfill, Rollback 스크립트를 같은 버전 번호로 관리 |
| 검증 | staging 배포 전에 Dry-run, Checksum 검증, 필수 테이블/인덱스 존재 여부 검사 |
| Query 호환 | `Config/Query/xml`에서 변경되는 Query ID는 기존 버전과 신규 버전을 병행 유지하고 화면 전환 완료 후 정리 |
| 실패 처리 | Migration 실패 시 즉시 중단하고 이전 성공 버전 이후 스크립트만 롤백 후보로 표시 |

DB 변경은 자동 실행하되 운영에서는 적용 전 승인 단계를 둔다. 데이터 삭제나 대량 Backfill은 별도 Change Request와 수행 시간대를 지정한다.

### 16.5 클라이언트 배포

| 단계 | 설계 |
|------|------|
| DLL 서명 | Release 빌드된 WinForms DLL과 공통 DLL에 코드 서명 인증서를 적용 |
| Manifest 생성 | 파일명, SHA-256, 버전, 대상 UIID, 필수 여부를 `client-manifest.json`에 기록 |
| 업로드 | 현행 `DeployFileUpload`, 배포 이력 팝업 흐름과 호환되도록 배포 서버의 버전 디렉터리에 업로드 |
| 버전 파일 갱신 | `version.json` 또는 기존 배포 버전 파일에 `current`, `minimumSupported`, `rollback` 버전을 기록 |
| 클라이언트 갱신 | 로그인 또는 앱 시작 시 Manifest를 비교해 변경 DLL만 다운로드하고, 실행 중 파일은 다음 재시작 시 교체 |
| 감사 | 배포 요청자, 승인자, 버전, 파일 Hash, 대상 환경을 배포 이력 테이블에 저장 |

### 16.6 롤백 계획

| 대상 | 절차 |
|------|------|
| API | Blue-Green 라우팅을 이전 Blue로 즉시 복원한다. Green 배포 실패 시 트래픽 전환을 하지 않는다. |
| 클라이언트 | `version.json.current`를 직전 버전으로 되돌리고 Manifest의 `rollback` 버전을 활성화한다. 클라이언트는 다음 시작 시 이전 DLL을 복원한다. |
| DB | 하위 호환 Migration은 코드만 롤백한다. 비호환 Migration은 사전 정의된 Rollback Script를 실행하되 데이터 손실 가능 항목은 운영 승인 후 처리한다. |
| 캐시 | 롤백 직후 전체 `lang:*`, `menu:*`, `code:*`, `auth:*` 캐시를 무효화한다. |
| 검증 | 롤백 후 `/health/ready`, 로그인, 메뉴 로드, 코드 콤보, 대표 저장 Rule Smoke Test를 실행한다. |

---

## 17. 헬스체크 / 모니터링 / Metrics 설계

운영 관측성은 API 상태, 외부 의존성, 사용자 요청 추적, 업무 지표를 분리해 수집한다. 모든 요청은 `CorrelationId`를 기준으로 로그, Trace, Metrics를 연결한다.

### 17.1 HealthCheck 엔드포인트

| 엔드포인트 | 목적 | 포함 항목 |
|------|------|------|
| `/health/live` | 프로세스 생존 확인. Orchestrator가 재시작 여부 판단 | 자체 프로세스, 메모리 임계치, ThreadPool 고갈 여부 |
| `/health/ready` | 트래픽 수신 가능 여부 판단 | DB, Kafka, SignalR Hub, 외부 설비 통신 채널, 필수 설정, Migration 상태 |
| `/health/startup` | 배포 직후 초기화 완료 여부 | Query Provider 로드, Cache Warm-up, 배포 Manifest 접근, 필수 Secret 로드 |

`live`는 외부 의존성 장애로 실패시키지 않는다. `ready`는 사용자가 정상 업무를 수행할 수 없는 의존성 장애를 실패로 표시해 트래픽 유입을 차단한다.

### 17.2 의존성 체크 항목

| 의존성 | 체크 방식 | 실패 기준 |
|------|------|------|
| DB | `SELECT 1`, Migration History 최신 버전 확인, 주요 Schema 존재 확인 | 연결 실패, Timeout, 필수 Migration 누락 |
| Kafka | Broker Metadata 조회, Producer 테스트 Topic 권한 확인 | Broker 연결 실패, 인증 실패, Topic 없음 |
| SignalR | Hub Endpoint 초기화, Backplane 사용 시 Redis 연결 확인 | Hub 시작 실패, Backplane 연결 실패 |
| 외부 설비 통신 채널 | 설비 Adapter별 TCP/HTTP 연결 상태, 마지막 송수신 시각, Heartbeat | 필수 설비 채널 연결 실패, Heartbeat 지연 |
| 파일/배포 저장소 | 클라이언트 Manifest 읽기, 업로드 경로 쓰기 권한 확인 | Manifest 접근 실패, 저장소 쓰기 불가 |
| Redis 확장 시 | Ping, Pub/Sub 구독 상태, Cache Read/Write 샘플 | 연결 실패, 직렬화 실패 |

설비 통신은 모든 설비가 장애라고 API 전체를 NotReady로 만들지 않는다. 필수 라인/Plant별 채널만 Ready 판단에 포함하고, 개별 설비 장애는 Metrics와 알람으로 분리한다.

### 17.3 Serilog 구조화 로그

로그는 JSON 형식으로 수집하고 다음 필드를 공통 Enricher로 주입한다.

| 필드 | 설명 |
|------|------|
| `CorrelationId` | 외부 요청 Header `X-Correlation-Id` 또는 서버 생성 ID |
| `TraceId`, `SpanId` | OpenTelemetry Trace Context |
| `UserId` | 인증 사용자. 비로그인 요청은 `anonymous` |
| `PlantId` | 요청 Plant. Plant 없는 System 데이터는 `GLOBAL` |
| `UiId` | 클라이언트 UI ID |
| `MenuId` | 화면 또는 팝업 메뉴 ID |
| `RuleId` | 실행 Rule ID 또는 Application Service 명 |
| `QueryId` | 실행 Query ID와 Version |
| `TxnHistKey` | 기존 감사 이력과 연결되는 Transaction Key |
| `ElapsedMs` | 요청 또는 Rule 실행 시간 |
| `ResultCode` | 기존 응답 코드 호환. 성공은 `0` |

API 요청 로그, Rule 실행 로그, Query 실행 로그는 같은 `CorrelationId`를 공유한다. 오류 로그에는 사용자 표시 메시지와 내부 예외를 분리하고, 개인정보와 설비 민감 Payload는 Masking한다.

구현은 본 절을 다음과 같이 적응한다. Serilog는 `Serilog.AspNetCore` 단일 패키지(Console/File/CompactJson 번들 포함)로 구성하고, 싱크와 레벨은 appsettings의 `Serilog` 섹션으로 정의한다 — File 싱크는 JSON 수집 요구에 맞춰 `CompactJsonFormatter`(일 단위 롤링, 31일 보관)를 사용한다. 공통 필드 중 `CorrelationId`/`UserId`/`PlantId`는 인증 직후 배치된 `RequestLogContextMiddleware`가 `LogContext`로 주입하고(클라이언트 제공 CorrelationId는 길이 64자·영숫자/`-_.:`만 허용하며 통과하지 못하면 서버가 재생성한다 — 응답 Header와 모든 로그에 그대로 실리는 값이라 로그 오염을 차단), `ElapsedMs`/StatusCode는 `UseSerilogRequestLogging`의 요청 완료 로그가 담당한다. `UiId`/`MenuId`/`RuleId`/`QueryId`/`TxnHistKey`는 해당 개념(화면 메타데이터, Rule/Query 실행기)이 아직 도입되지 않아 보류하며, 도입 시점에 같은 미들웨어 방식으로 추가한다. §17.4의 OpenTelemetry(`TraceId`/`SpanId`) 역시 추적 인프라 도입 시점의 과제로 미룬다.

### 17.4 OpenTelemetry 분산 추적

| 대상 | 설계 |
|------|------|
| 요청 진입 | API Middleware가 `traceparent`, `X-Correlation-Id`를 수신하고 없으면 생성 |
| 클라이언트 전파 | WinForms `MessageWorker`와 신규 HTTP Client가 `X-Correlation-Id`, `traceparent`를 Header에 포함 |
| 내부 호출 | Rule Service, Query Provider, CacheService, Kafka Producer, SignalR Hub 호출을 Span으로 기록 |
| 외부 설비 통신 | 설비 메시지 송신, Reply 수신, Timeout을 별도 Span으로 기록하고 EquipmentId를 Attribute로 추가 |
| Trace-Request-Response | 주요 API는 Request DTO 요약, Response Code, Row Count, ErrorCode를 Attribute로 남기고 Body 전문은 저장하지 않음 |
| Exporter | 개발은 Console/OTLP, 운영은 OTLP Collector를 통해 Jaeger, Tempo, Application Insights 중 운영 표준으로 전송 |

Cache Hit/Miss는 Span Event로 남긴다. 다만 Dictionary와 Code처럼 호출 빈도가 높은 조회는 Sampling 정책을 적용해 Trace 저장량을 제한한다.

### 17.5 비즈니스 Metrics

| Metric | Type | Label | 설명 |
|------|------|------|------|
| `nexames_active_users` | Gauge | `plantId`, `uiId` | 최근 5분 내 요청 또는 SignalR 연결이 있는 사용자 수 |
| `nexames_api_request_duration_ms` | Histogram | `route`, `method`, `plantId`, `resultCode` | API 응답시간 |
| `nexames_api_error_rate` | Counter/Rate | `route`, `errorCode`, `plantId` | 오류율 산정용 오류 Count |
| `nexames_rule_duration_ms` | Histogram | `ruleId`, `menuId`, `plantId` | Rule/Application Service 실행 시간 |
| `nexames_query_duration_ms` | Histogram | `queryId`, `dbms`, `plantId` | Query 실행 시간 |
| `nexames_cache_hit_total` | Counter | `area`, `plantId` | 캐시 Hit |
| `nexames_cache_miss_total` | Counter | `area`, `plantId` | 캐시 Miss |
| `nexames_fdc_collection_rate` | Gauge | `plantId`, `equipmentId`, `parameterGroup` | 기대 수집 건수 대비 실제 FDC 수집률 |
| `nexames_equipment_channel_status` | Gauge | `plantId`, `equipmentId`, `channelType` | 설비 채널 연결 상태. 정상 1, 비정상 0 |
| `nexames_client_deploy_version` | Gauge | `uiId`, `version` | 활성 클라이언트 배포 버전 |

Metrics는 Prometheus 형식을 기본으로 노출하고, 운영 표준이 Azure Monitor 또는 CloudWatch인 경우 Collector에서 변환한다.

### 17.6 운영 대시보드 구성 기준

| 대시보드 | 주요 패널 |
|------|------|
| API Overview | 요청량, P50/P95/P99 응답시간, 오류율, 상위 ErrorCode, Ready 상태 |
| User/Plant | Plant별 활성 사용자 수, UIID별 접속 수, 메뉴별 사용량 |
| Rule/Query | 느린 Rule Top N, 느린 Query Top N, DB Timeout, Query ID별 실패율 |
| Cache | Area별 Hit Ratio, Miss Spike, 무효화 이벤트 수, Redis 전환 시 연결 상태 |
| FDC/Equipment | FDC 수집률, 설비 채널 상태, Heartbeat 지연, Interlock/Alarm 이벤트 |
| Deployment | 현재 API 버전, 클라이언트 DLL 버전, DB Migration 버전, 최근 배포/롤백 이력 |
| Health | `/health/live`, `/health/ready`, 의존성별 상태, 최근 NotReady 원인 |

알람은 사용자 영향 기준으로 설정한다. 예를 들어 `/health/ready` 2분 이상 실패, API 오류율 5분 평균 3% 초과, P95 응답시간 2초 초과, FDC 수집률 95% 미만, 필수 설비 채널 Heartbeat 지연을 운영 알람 기준으로 삼는다.

## 18. 아키텍처 보완 설계

본 장은 현행 `reference/SmartUX3.5_20260526` 소스의 Java OSGi 번들, `@AComponent` Rule, SO(Data/Key) 클래스, `Config/Settings/communication/config.properties`, `Config/Datasource/*-datasource.json`, `Config/Message/websocket-events.xml` 구조를 .NET 8 기반 설계로 치환하기 위한 보완 명세이다. 현행 `s-communication-ees.ept`, `s-communication-ees.fdc`, `s-communication-ees.rms`는 설비 통신 Bounded Context로 보고, `s-component-factory.api`, `s-component-factory.so`, `s-component-ees.api`, `s-component-ees.so`, `s-component-standard.so`의 업무 객체를 Domain/Application/Infrastructure 경계로 분리한다.

### 18.1 Clean Architecture / DDD 레이어 경계 명세

#### 18.1.1 프로젝트/레이어 경계

| 레이어 | 신규 프로젝트 예시 | 책임 | 참조 허용 |
|------|------|------|------|
| Domain | `NexaMes.Domain` | Aggregate, Entity, Value Object, Domain Event, Repository Interface, Domain Service | 없음. 외부 프레임워크 의존 금지 |
| Application | `NexaMes.Application` | Use Case, Transaction Boundary, DTO, Command/Query Handler, UoW 호출, ACL Port | `Domain` |
| Infrastructure | `NexaMes.Infrastructure` | EF Core/Dapper Repository 구현, Kafka/Redis/SignalR/Ocelot/Polly, 외부 설비 Adapter, 파일/DLL 로딩 | `Application`, `Domain` |
| Presentation | `NexaMes.Api`, `NexaMes.WinForms`, `NexaMes.Web` | Controller/Hub/UI, 인증/인가, API Version, Rate Limit 진입점 | `Application` |
| Legacy ACL | `NexaMes.LegacyAcl` | Java `IData`, `MessageFormat(head/body/transaction/result)`, SO Data/Key 명칭을 C# DTO/Command로 변환 | `Application`, `Domain` |

Domain은 DB 테이블명, Java SO 클래스명, SignalR, Kafka, EF Core를 알지 않는다. 예를 들어 현행 `WpmTbLotData`, `PpmTbWorkOrderData`, `FdcTbParameterData`는 Infrastructure의 영속성 모델 또는 ACL 입력 모델로만 사용하고, Domain에서는 `Lot`, `WorkOrder`, `FdcParameter` Aggregate로 표현한다.

#### 18.1.2 Aggregate Root / Entity / Value Object

| Aggregate Root | 현행 근거 | 포함 Entity | 주요 Value Object | 불변식 |
|------|------|------|------|------|
| `Equipment` | `StdTbEquipmentData`, `MdmTbEquipmentData`, `EptTbEquipmentStatusData`, `CurrentEquipmentStatus`, `SubEquipmentInfo` | `SubEquipment`, `EquipmentStatus`, `EquipmentAlarm`, `EquipmentStateSummary` | `EquipmentId`, `PlantId`, `EquipmentState`, `AlarmId`, `IpAddress` | Plant 내 EquipmentId 유일, 상태 전이는 State Matrix 검증 후 수행, SubEquipment는 부모 설비 없이 존재 불가 |
| `Lot` | `WpmTbLotData`, `WpmTbSubLotData`, `WpmTbLotTraceData`, `WpmTbLotHoldData`, `WpmTbLotRepairData`, `QmsTbLotHoldData` | `SubLot`, `LotTrace`, `LotHold`, `LotDefect`, `LotScrap`, `CarrierLot` | `LotId`, `SubLotId`, `SegmentId`, `ProcessState`, `HoldState` | Hold 상태 Lot은 TrackIn/TrackOut 불가, TrackIn은 `InProduction` 상태와 설비 작업 가능 조건 필요 |
| `Recipe` | `RmsTbProcessRecipeData`, `RmsTbSequenceRecipeData`, `RmsTbRecipeParameterData`, `RmsTbEquipmentRecipeHistData`, `EqpTbRecipeDefData` | `RecipeParameter`, `SequenceRecipe`, `RecipeValidation`, `RecipeDownloadHistory` | `RecipeId`, `RecipeVersion`, `ItemDefId`, `ParameterId` | 승인되지 않은 Recipe는 설비 Download 불가, Parameter Spec 범위 위반 시 승인/배포 차단 |
| `FdcParameter` | `FdcTbParameterData`, `FdcTbTraceGroupData`, `FdcTbSummaryParameterData`, `FdcTb*SpecData`, `FdcTbInterlockHistData`, `CurrentParameterInfo` | `TraceGroup`, `TraceData`, `SummaryData`, `ParameterSpec`, `InterlockHistory` | `ParameterId`, `TraceRequestId`, `SpecLimit`, `InterlockAction` | Spec Out + DoInterlock=Y이면 Interlock 이력과 설비/LOT Hold 명령 생성 |
| `WorkOrder` | `PpmTbWorkOrderData`, `PpmTbWorkOrderHistData`, `REQUEST_WORK_ORDER`, `REPORT_WORK_ORDER` | `WorkOrderHistory`, `WorkOrderMaterial`, `WorkOrderProgress` | `WorkOrderId`, `ItemDefId`, `OrderState`, `Quantity` | Confirm 전 Start 불가, Hold 상태 WorkOrder는 Start/Finish 불가 |

Value Object는 `record struct` 또는 불변 `record`로 정의하고 생성 시 검증한다.

```csharp
public readonly record struct PlantId
{
    public string Value { get; }
    public PlantId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException("PlantId is required.");
        Value = value.Trim();
    }
    public override string ToString() => Value;
}

public readonly record struct EquipmentId
{
    public string Value { get; }
    public EquipmentId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainException("EquipmentId is required.");
        Value = value.Trim();
    }
    public override string ToString() => Value;
}

public enum LanguageType
{
    KoKr,
    EnUs,
    ZhCn,
    ViVn,
    LoLo   // 라오어 (lo-LO) — 동남아 현장 지원
}
```

#### 18.1.3 Domain Event 설계

| Event | 발생 Aggregate | 현행 Trigger/근거 | 주요 Payload | 후속 처리 |
|------|------|------|------|------|
| `EquipmentStateChanged` | `Equipment` | `REPORT_EQUIPMENT_STATE`, `EquipmentStateService.execute`, `StateService.changeEquipmentState` | `PlantId`, `EquipmentId`, `FromState`, `ToState`, `EventTime`, `TransactionId`, `IsSubEquipment` | 상태 이력 저장, 상태 Summary 갱신, SignalR 설비 그룹 알림 |
| `EquipmentAlarmRaised` | `Equipment` | `REPORT_ALARM_STATE`, `EquipmentAlarmService`, `EptTbAlarmData` | `AlarmId`, `AlarmLevel`, `DoInterlock`, `ActionCode` | Alarm 이력 저장, Interlock 대상이면 설비 Hold 명령 |
| `FdcInterlockTriggered` | `FdcParameter` | `REPORT_FDC_SPEC_CHECK`, `SpecValidationService.InterlockAction`, `FdcTbInterlockHistData` | `ParameterId`, `ParameterValue`, `SpecLimit`, `InterlockType`, `InterlockAction`, `LotId`, `RecipeId` | `REQUEST_FDC_INTERLOCK` 송신, Lot/Equipment Hold, 메일/SignalR 고우선 알림 |
| `RecipeApproved` | `Recipe` | RMS Recipe 승인/검증 이력, `RmsTbRecipeValidation*`, `RmsTbProcessRecipe*` | `RecipeId`, `RecipeVersion`, `ApprovedBy`, `ApprovedAt`, `ParameterHash` | Recipe Download 허용, 감사 이력, 설비별 배포 후보 갱신 |
| `RecipeDownloaded` | `Recipe` | `REQUEST_DOWNLOAD_RECIPE`, `REPLY_DOWNLOAD_RECIPE`, `RmsTbRecipeDownload*HistData` | `EquipmentId`, `RecipeId`, `RecipeVersion`, `ResultCode` | Download 이력 저장, 실패 시 재시도/알람 |
| `LotTrackedIn` | `Lot` | `TrackInLotService.trackInLot`, `LotTraceService.createTrackInTrace` | `LotId`, `EquipmentId`, `SegmentId`, `TrackInTime`, `OperatorId` | Lot Trace 저장, 설비 현재 Lot 갱신, UI 진행 상태 알림 |
| `WorkOrderStarted` | `WorkOrder` | `StartWorkOrderService`, `REQUEST_WORK_ORDER`/`REPORT_WORK_ORDER` | `WorkOrderId`, `PlantId`, `ItemDefId`, `StartTime`, `Quantity` | 작업지시 이력 저장, LOT 생성/연결 Use Case 호출 |

Domain Event는 Aggregate 내부에서 수집하고 Application Service가 UoW Commit 직전 Outbox에 저장한다. 외부 발행(Kafka/SignalR/Email)은 Commit 이후 Background Outbox Publisher가 처리한다.

#### 18.1.4 Repository Interface

Repository 인터페이스는 Domain 계층에 선언한다. 구현은 `NexaMes.Infrastructure.Persistence`에서 EF Core 또는 Dapper로 제공한다.

```csharp
public interface IEquipmentRepository
{
    Task<Equipment?> GetAsync(PlantId plantId, EquipmentId equipmentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Equipment>> FindByPlantAsync(PlantId plantId, CancellationToken cancellationToken);
    Task SaveAsync(Equipment equipment, CancellationToken cancellationToken);
}

public interface ILotRepository
{
    Task<Lot?> GetAsync(PlantId plantId, LotId lotId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Lot>> FindRunningLotsAsync(PlantId plantId, EquipmentId equipmentId, CancellationToken cancellationToken);
    Task SaveAsync(Lot lot, CancellationToken cancellationToken);
}

public interface IRecipeRepository
{
    Task<Recipe?> GetAsync(PlantId plantId, RecipeId recipeId, RecipeVersion version, CancellationToken cancellationToken);
    Task SaveAsync(Recipe recipe, CancellationToken cancellationToken);
}

public interface IFdcParameterRepository
{
    Task<FdcParameter?> GetAsync(PlantId plantId, EquipmentId equipmentId, ParameterId parameterId, CancellationToken cancellationToken);
    Task SaveAsync(FdcParameter parameter, CancellationToken cancellationToken);
}

public interface IWorkOrderRepository
{
    Task<WorkOrder?> GetAsync(PlantId plantId, WorkOrderId workOrderId, CancellationToken cancellationToken);
    Task SaveAsync(WorkOrder workOrder, CancellationToken cancellationToken);
}
```

#### 18.1.5 Application Service 트랜잭션 경계

원칙은 "단일 Use Case = 단일 트랜잭션"이다. Controller, Hub, UI 이벤트는 트랜잭션을 시작하지 않고 Application Service만 UoW를 호출한다. Query 전용 API는 읽기 전용 Connection/Transaction을 사용하거나 DBMS별 Snapshot 정책을 따른다.

```csharp
public interface IUnitOfWork
{
    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken);
    Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> action, CancellationToken cancellationToken);
}

public sealed class TrackInLotUseCase
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILotRepository _lots;
    private readonly IEquipmentRepository _equipment;

    public TrackInLotUseCase(IUnitOfWork unitOfWork, ILotRepository lots, IEquipmentRepository equipment)
    {
        _unitOfWork = unitOfWork;
        _lots = lots;
        _equipment = equipment;
    }

    public Task ExecuteAsync(TrackInLotCommand command, CancellationToken cancellationToken)
    {
        return _unitOfWork.ExecuteAsync(async ct =>
        {
            var plantId = new PlantId(command.PlantId);
            var lot = await _lots.GetAsync(plantId, new LotId(command.LotId), ct)
                ?? throw new NotFoundException("Lot", command.LotId);
            var equipment = await _equipment.GetAsync(plantId, new EquipmentId(command.EquipmentId), ct)
                ?? throw new NotFoundException("Equipment", command.EquipmentId);

            lot.TrackIn(equipment.Id, command.OperatorId, command.EventTime);

            await _lots.SaveAsync(lot, ct);
            await _equipment.SaveAsync(equipment, ct);
        }, cancellationToken);
    }
}
```

`TrackInLotService`의 현행 검증(`LotState == InProduction`, `IsHold == N`)은 `Lot.TrackIn` Domain Method로 이동하고, `LotTraceService.createTrackInTrace`는 Domain Event `LotTrackedIn` 처리기로 분리한다.

#### 18.1.6 Anti-Corruption Layer

현행 Java 메시지는 `MessageFormat.Head`, `Body`, `Transaction`, `Result`와 `IData` 동적 필드(`PLNTID`, `PLANT_ID`, `EQPID`, `PRODUCTINFO`, `FDCPARAMETER`)가 혼재한다. .NET에서는 Legacy ACL이 외부/기존 명칭을 신규 Canonical DTO로 변환한다.

| 현행 패턴 | 신규 ACL 타입 | 변환 규칙 |
|------|------|------|
| `IData`, `DataRepository.create()` | `LegacyMessageEnvelope` | `Head`, `Body`, `Transaction`, `Result`를 명시적 record로 역직렬화 |
| `MessageConverter.getDataFromTcMessage(body)` | `TcMessageNormalizer` | `PLNTID`/`PLANT_ID`, `EQPID`/`EQUIPMENTID`, `SUBEQPID`/`SUB_EQUIPMENT_ID` 동의어를 표준 필드로 정규화 |
| `Mapper(SomeSet.class, data)` | `LegacyFieldMapper<TCommand>` | Java enum field set을 C# DTO property 매핑 테이블로 대체 |
| SO `*Data`/`*Key` | `LegacySoRow` 또는 EF Entity | DB 스키마 필드명은 Infrastructure에 격리하고 Domain Entity와 직접 공유하지 않음 |
| `QueryProvider.select("GetTraceGroupParameterMap", "00001", parameter)` | `ITraceGroupQuery.GetParameterMapAsync` | Query ID/Version은 Query Object에 캡슐화 |
| `MessageDispatcher.context().destination("REQUEST_FDC_INTERLOCK")` | `IEquipmentCommandBus.SendAsync` | 목적지 문자열은 `EquipmentMessageType` enum으로 변환 |

```csharp
public sealed class EquipmentStateMessageAcl
{
    public EquipmentStateChangedCommand ToCommand(LegacyMessageEnvelope envelope)
    {
        var body = TcMessageNormalizer.Normalize(envelope.Body);
        return new EquipmentStateChangedCommand(
            PlantId: body.Require("PLANT_ID"),
            EquipmentId: body.Require("EQUIPMENT_ID"),
            SubEquipmentId: body.GetOptional("SUB_EQUIPMENT_ID"),
            StateId: body.Require("STATE_ID"),
            TransactionId: envelope.Transaction.Require("id"),
            EventTime: envelope.Transaction.GetEventTime());
    }
}
```

### 18.2 API Gateway / Rate Limiting / Circuit Breaker

#### 18.2.1 단일 ASP.NET Core Host + Ocelot Gateway

초기 전환 단계에서는 별도 Gateway 프로세스를 두지 않고 단일 ASP.NET Core Host에서 Ocelot 미들웨어가 외부 진입점을 담당한다. Host 내부 모듈은 Controller/Minimal API Endpoint Group으로 분리하되, 외부 공개 URL은 Ocelot route 정책으로 고정한다.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("ocelot.json", optional: false, reloadOnChange: true);
builder.Services.AddControllers();
builder.Services.AddOcelot(builder.Configuration);
builder.Services.AddRateLimiter(ConfigureRateLimiter);
builder.Services.AddSignalR();

var app = builder.Build();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapHub<EquipmentHub>("/hubs/equipment");

await app.UseOcelot();
await app.RunAsync();
```

Ocelot route는 클라이언트 타입과 API 버전을 모두 포함한다.

```json
{
  "Routes": [
    {
      "UpstreamPathTemplate": "/api/v1/winforms/{everything}",
      "UpstreamHttpMethod": [ "GET", "POST", "PUT", "DELETE" ],
      "DownstreamPathTemplate": "/internal/v1/winforms/{everything}",
      "DownstreamScheme": "http",
      "DownstreamHostAndPorts": [ { "Host": "127.0.0.1", "Port": 5000 } ]
    },
    {
      "UpstreamPathTemplate": "/api/v1/web/{everything}",
      "UpstreamHttpMethod": [ "GET", "POST" ],
      "DownstreamPathTemplate": "/internal/v1/web/{everything}",
      "DownstreamScheme": "http",
      "DownstreamHostAndPorts": [ { "Host": "127.0.0.1", "Port": 5000 } ]
    }
  ]
}
```

#### 18.2.2 클라이언트 타입별 엔드포인트 분리

| 클라이언트 | Prefix | 목적 | 호환 정책 |
|------|------|------|------|
| WinForms 전용 | `/api/v1/winforms/*` | 기존 `MessageWorker`, 동적 DLL, 레거시 Grid Save, Excel Import/Export | 기존 ResultCode/Message 구조 유지, 대량 Payload 허용 |
| Web 전용 | `/api/v1/web/*` | 신규 Web 화면, SignalR 구독, REST Query | 표준 ProblemDetails, Pagination/Cursor, DTO 명시 |
| 공통 System | `/api/v1/system/*` | 인증, 코드, 다국어, Health, 배포 Manifest | 클라이언트 공통 계약 |
| 설비/통신 내부 | `/internal/v1/equipment/*` | Adapter Callback, Kafka Consumer 내부 호출 | 외부 Gateway에서 직접 노출 금지 |

#### 18.2.3 Rate Limiting

ASP.NET Core 8 내장 `RateLimiter`를 사용하고 기본 정책은 사용자별 100req/min이다. 인증 사용자가 없으면 IP 기준으로 Partition을 나눈다. Excel Export, File Upload처럼 긴 작업은 별도 정책(`bulk`)으로 분리한다.

```csharp
static void ConfigureRateLimiter(RateLimiterOptions options)
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var userKey = context.User.Identity?.IsAuthenticated == true
            ? context.User.Identity.Name!
            : context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: userKey,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
}
```

구현은 본 절을 다음과 같이 적응한다. 파티션 키는 위 표준 코드의 `Identity.Name` 대신 JWT의 `NameIdentifier` 클레임을 사용한다 — 현재 토큰 발급기가 Name 클레임을 싣지 않기 때문이며, 클레임이 없으면 원격 IP, 그것도 없으면 `"anonymous"`로 폴백한다. 전역 한도(100req/min, FixedWindow, QueueLimit 0)에 더해 `"auth"` 명명 정책(IP당 10req/min)을 신설하여 login·refresh·forgot-password·reset-password·`exists/{userId}`·등록 신청(request) 등 익명 진입점 컨트롤러 액션에 `[EnableRateLimiting("auth")]`로 적용한다 — 브루트포스와 계정 열거 시도를 인증 전 단계에서 차단하기 위함이다. 본문이 예고한 `bulk` 정책은 Excel Export/File Upload 기능이 아직 없으므로 해당 기능 도입 시점으로 보류한다. 반대로 SignalR 허브(`/hubs/smartees`)와 헬스 프로브(`/health`)는 `DisableRateLimiting()`으로 전역 한도에서 제외한다 — long-polling 폴백 시 분당 요청 수가 급증하는 SignalR과 공유 모니터링 IP에서 호출되는 헬스 체크가 429로 끊기면 안 되기 때문이다. 거부 응답은 본문대로 429를 유지한다.

#### 18.2.4 Circuit Breaker

Polly는 DB, Kafka, Redis, 외부 설비 Adapter 호출에 적용한다. Domain/Application은 Polly를 알지 않고 Infrastructure Adapter에서 정책을 주입한다.

| 대상 | 정책명 | 실패 기준 | Open 시간 | 비고 |
|------|------|------|------|------|
| DB | `db-command` | Timeout, Deadlock, transient network error | 30초 | Transaction 내부 재시도는 멱등 Use Case만 허용 |
| Kafka | `kafka-producer`, `kafka-consumer` | Broker 연결 실패, Produce timeout | 60초 | Outbox 재발행과 연계 |
| Redis/SignalR Backplane | `redis-backplane` | Ping 실패, Pub/Sub 실패 | 30초 | Backplane 장애 시 단일 서버 in-memory로 degrade |
| 외부 설비 | `equipment-io` | Connect/Send/Receive timeout, NACK | 10초 후 half-open | 설비별 Circuit 분리 |

```csharp
builder.Services.AddResiliencePipeline("equipment-io", pipeline =>
{
    pipeline.AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromMilliseconds(200),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true
    });

    pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        FailureRatio = 0.5,
        MinimumThroughput = 20,
        SamplingDuration = TimeSpan.FromSeconds(30),
        BreakDuration = TimeSpan.FromSeconds(10)
    });
});
```

#### 18.2.5 API 버전 관리

API 버전은 URL Path 방식(`/api/v1`, `/api/v2`)을 표준으로 한다. `v1`은 현행 SmartUX3.5 호환 DTO와 ResultCode를 유지하고, `v2`는 정규화된 REST DTO, ProblemDetails, Cursor Pagination을 적용한다.

| 항목 | v1 | v2 |
|------|------|------|
| 오류 응답 | `{ code, errcode, message }` | RFC 7807 `ProblemDetails` + 업무 ErrorCode |
| 날짜 | 현행 `yyyyMMddHHmmssSSS` 수용 | ISO-8601 UTC |
| 설비 메시지 | `PLNTID`, `EQPID` 동의어 수용 | `plantId`, `equipmentId`만 허용 |
| 대량 저장 | 기존 Grid `upsertSet` 호환 | 명시 Command DTO |
| 폐기 정책 | 마이그레이션 기간 유지 | 신규 기능 기본 |

### 18.3 비동기 처리 표준화

#### 18.3.1 CancellationToken 필수

모든 Service/Repository/Adapter async 메서드는 마지막 파라미터로 `CancellationToken`을 받는다. `CancellationToken.None` 고정 사용은 금지하고, UI/HTTP/BackgroundService의 토큰을 끝까지 전파한다.

```csharp
public interface IEquipmentApplicationService
{
    Task ChangeStateAsync(ChangeEquipmentStateCommand command, CancellationToken cancellationToken);
}

public interface IEquipmentStatusQuery
{
    Task<EquipmentStatusDto?> GetCurrentAsync(PlantId plantId, EquipmentId equipmentId, CancellationToken cancellationToken);
}
```

#### 18.3.2 UI 레이어 이벤트 처리

WinForms 이벤트 시그니처는 `void`가 필요하지만 `async void` 키워드는 사용하지 않는다. 공통 Runner가 예외 처리, UI Busy 상태, 취소 토큰을 관리한다.

```csharp
private void saveButton_Click(object? sender, EventArgs e)
{
    _ = RunUiEventAsync(ct => SaveAsync(ct));
}

private async Task RunUiEventAsync(Func<CancellationToken, Task> action)
{
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(_formClosing.Token);
    try
    {
        SetBusy(true);
        await action(cts.Token).ConfigureAwait(true);
    }
    catch (OperationCanceledException)
    {
        ShowStatus("작업이 취소되었습니다.");
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "UI event failed.");
        ShowError(ex);
    }
    finally
    {
        SetBusy(false);
    }
}
```

#### 18.3.3 FDC 실시간 수집 동시성 제어

현행 FDC는 `REPORT_FDC_PARAMETER`, `REPORT_FDC_SUMMARY_PARAMETER`, `REPORT_FDC_SPEC_CHECK`, `GEN_FDC_DATA`가 짧은 주기로 유입될 수 있다. 설비/Plant별 처리 폭주를 막기 위해 `SemaphoreSlim`을 Adapter 또는 Application Handler 앞단에 둔다.

```csharp
public sealed class FdcIngestionLimiter
{
    private readonly SemaphoreSlim _gate;

    public FdcIngestionLimiter(IOptions<FdcOptions> options)
    {
        _gate = new SemaphoreSlim(options.Value.MaxConcurrentIngestion, options.Value.MaxConcurrentIngestion);
    }

    public async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await action(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
```

기본값은 서버당 32개, Plant별 8개로 시작하고 Metrics(`nexames_fdc_ingestion_queue_length`, `nexames_fdc_ingestion_duration_ms`)를 보고 조정한다.

#### 18.3.4 IProgress<T> 표준

Excel Import/Export, 대량 Grid Save, Recipe Parameter 일괄 다운로드처럼 사용자 대기 시간이 긴 작업은 `IProgress<T>`로 진행률을 보고한다. Web은 SignalR로, WinForms는 Progress UI로 연결한다.

```csharp
public sealed record BulkProgress(int Total, int Completed, string Stage, string? CurrentKey);

public async Task ImportLotsAsync(
    Stream excel,
    IProgress<BulkProgress> progress,
    CancellationToken cancellationToken)
{
    var rows = await _excel.ReadRowsAsync(excel, cancellationToken);
    for (var i = 0; i < rows.Count; i++)
    {
        await _lotImporter.ImportRowAsync(rows[i], cancellationToken);
        progress.Report(new BulkProgress(rows.Count, i + 1, "ImportLot", rows[i].LotId));
    }
}
```

#### 18.3.5 BackgroundService vs IHostedService

| 구분 | 사용 대상 | 예시 | 주의 |
|------|------|------|------|
| `BackgroundService` | 애플리케이션 실행 중 반복/상시 수행 | Kafka Consumer, Equipment Adapter Receive Loop, FDC Ingestion Queue, Outbox Publisher, Health Heartbeat | `ExecuteAsync`에서 `stoppingToken` 필수 사용, 예외 발생 시 루프 복구 정책 명시 |
| `IHostedService` | 시작/종료 시 1회성 작업 | DB Migration 확인, Cache Warm-up, Adapter Registry 초기화, DLL Manifest 검증 | 장시간 Blocking 금지, 실패 시 Host 시작 중단 여부 명확화 |
| `PeriodicTimer` 기반 Worker | 주기 Polling | 설비 Heartbeat 확인, Recipe Sync, 배포 Manifest Polling | Timer 중첩 실행 방지 |
| Queue Worker | 사용자 요청에서 분리된 비동기 작업 | Excel Export, 대량 저장 후 알림, 메일 발송 | 작업 상태 저장소와 재시도 정책 필요 |

### 18.4 외부 설비 통신(EQ I/F) 아키텍처

#### 18.4.1 Adapter 인터페이스

현행 `Config/Settings/communication/config.properties`는 `nio.driver=kafka,http,websocket,serial,socket`, WebSocket 서버 포트 `19020`, Jetty 포트 `9020`, Kafka Consumer, Socket 서버, Redis 설정을 포함한다. .NET에서는 프로토콜별 Adapter를 같은 인터페이스로 통일한다.

```csharp
public interface IEquipmentCommunicationAdapter
{
    string AdapterName { get; }
    EquipmentEndpoint Endpoint { get; }

    ValueTask ConnectAsync(CancellationToken cancellationToken);
    ValueTask DisconnectAsync(CancellationToken cancellationToken);
    ValueTask SendAsync(EquipmentMessage message, CancellationToken cancellationToken);
    IAsyncEnumerable<EquipmentMessage> ReceiveAsync(CancellationToken cancellationToken);
    ValueTask<EquipmentChannelHealth> CheckHealthAsync(CancellationToken cancellationToken);
}

public sealed record EquipmentEndpoint(
    PlantId PlantId,
    EquipmentId EquipmentId,
    EquipmentProtocol Protocol,
    string Address,
    int? Port);
```

구현체는 `KafkaEquipmentAdapter`, `WebSocketEquipmentAdapter`, `SocketEquipmentAdapter`, `SerialEquipmentAdapter`, `HttpEquipmentAdapter`로 분리한다. Adapter는 Raw Message 송수신과 Health만 담당하고, 업무 처리는 Handler가 담당한다.

#### 18.4.2 메시지 타입별 C# Handler 목록

현행 Java rule class는 `@AComponent(name = "...")`의 메시지 ID를 Handler Key로 사용한다. 신규 Handler는 `IEquipmentMessageHandler`를 구현하고, Handler Registry가 메시지 ID로 라우팅한다.

```csharp
public interface IEquipmentMessageHandler
{
    EquipmentMessageType MessageType { get; }
    Task HandleAsync(EquipmentMessage message, CancellationToken cancellationToken);
}
```

EPT 모듈(`s-communication-ees.ept`) 매핑:

| Java Message ID | C# Handler |
|------|------|
| `REPLY_ALARM_INTERLOCK` | `ReplyAlarmInterlockHandler` |
| `REPLY_EQUIPMENT_STATE` | `ReplyEquipmentStateHandler` |
| `REPLY_MCC_INTERLOCK` | `ReplyMccInterlockHandler` |
| `REPLY_SET_DATETIME` | `ReplySetDateTimeHandler` |
| `REPLY_STATE_MATRIX` | `ReplyStateMatrixHandler` |
| `REPLY_STATE_MATRIX_EES` | `ReplyStateMatrixEesHandler` |
| `REPLY_STATE_MATRIX_TC` | `ReplyStateMatrixTcHandler` |
| `REPORT_ALARM_STATE` | `ReportAlarmStateHandler` |
| `REPORT_CONTROL_STATE` | `ReportControlStateHandler` |
| `REPORT_END_LOT` | `ReportEndLotHandler` |
| `REPORT_END_STEP` | `ReportEndStepHandler` |
| `REPORT_END_SUBLOT` | `ReportEndSublotHandler` |
| `REPORT_EQUIPMENT_STATE` | `ReportEquipmentStateHandler` |
| `REPORT_LOAD_COMPLETE` | `ReportLoadCompleteHandler` |
| `REPORT_START_LOT` | `ReportStartLotHandler` |
| `REPORT_START_STEP` | `ReportStartStepHandler` |
| `REPORT_START_SUBLOT` | `ReportStartSublotHandler` |
| `REPORT_TC_INFORMATION` | `ReportTcInformationHandler` |
| `REPORT_UNLOAD_COMPLETE` | `ReportUnloadCompleteHandler` |
| `REQUEST_ALARM_INTERLOCK` | `RequestAlarmInterlockHandler` |
| `REQUEST_EQUIPMENT_STATE` | `RequestEquipmentStateHandler` |
| `REQUEST_MCC_INTERLOCK` | `RequestMccInterlockHandler` |
| `REQUEST_SET_DATETIME` | `RequestSetDateTimeHandler` |
| `REQUEST_STATE_MATRIX` | `RequestStateMatrixHandler` |
| `REQUEST_STATE_MATRIX_EES` | `RequestStateMatrixEesHandler` |
| `REQUEST_STATE_MATRIX_TC` | `RequestStateMatrixTcHandler` |

FDC 모듈(`s-communication-ees.fdc`) 매핑:

| Java Message ID | C# Handler |
|------|------|
| `GEN_FDC_DATA` | `GenFdcDataHandler` |
| `REPLY_FDC_INTERLOCK` | `ReplyFdcInterlockHandler` |
| `REPLY_SET_FDC_PARAMETER` | `ReplySetFdcParameterHandler` |
| `REPORT_FDC_PARAMETER` | `ReportFdcParameterHandler` |
| `REPORT_FDC_SPEC_CHECK` | `ReportFdcSpecCheckHandler` |
| `REPORT_FDC_SUMMARY_PARAMETER` | `ReportFdcSummaryParameterHandler` |
| `REPORT_SUMMARY_PARAMETER` | `ReportSummaryParameterHandler` |
| `REQUEST_FDC_INTERLOCK` | `RequestFdcInterlockHandler` |
| `REQUEST_GET_FDC_PARAMETER` | `RequestGetFdcParameterHandler` |
| `REQUEST_SET_FDC_PARAMETER` | `RequestSetFdcParameterHandler` |

RMS 모듈(`s-communication-ees.rms`) 매핑:

| Java Message ID | C# Handler |
|------|------|
| `REPLY_CHANGE_RECIPE` | `ReplyChangeRecipeHandler` |
| `REPLY_CREATE_RECIPE` | `ReplyCreateRecipeHandler` |
| `REPLY_DELETE_RECIPE` | `ReplyDeleteRecipeHandler` |
| `REPLY_DELETE_RECIPE_EES` | `ReplyDeleteRecipeEesHandler` |
| `REPLY_DELETE_RECIPE_TC` | `ReplyDeleteRecipeTcHandler` |
| `REPLY_DOWNLOAD_RECIPE` | `ReplyDownloadRecipeHandler` |
| `REPLY_DOWNLOAD_SEQUENCE_RECIPE` | `ReplyDownloadSequenceRecipeHandler` |
| `REPLY_RECIPE_LIST` | `ReplyRecipeListHandler` |
| `REPLY_UPLOAD_RECIPE` | `ReplyUploadRecipeHandler` |
| `REPLY_UPLOAD_SEQUENCE_RECIPE` | `ReplyUploadSequenceRecipeHandler` |
| `REPLY_WORK_ORDER` | `ReplyWorkOrderHandler` |
| `REPORT_WORK_ORDER` | `ReportWorkOrderHandler` |
| `REQUEST_CHANGE_RECIPE` | `RequestChangeRecipeHandler` |
| `REQUEST_CHANGE_SEQUENCE_RECIPE` | `RequestChangeSequenceRecipeHandler` |
| `REQUEST_CREATE_RECIPE` | `RequestCreateRecipeHandler` |
| `REQUEST_CREATE_SEQUENCE_RECIPE` | `RequestCreateSequenceRecipeHandler` |
| `REQUEST_DELETE_RECIPE` | `RequestDeleteRecipeHandler` |
| `REQUEST_DELETE_RECIPE_EES` | `RequestDeleteRecipeEesHandler` |
| `REQUEST_DELETE_RECIPE_TC` | `RequestDeleteRecipeTcHandler` |
| `REQUEST_DOWNLOAD_RECIPE` | `RequestDownloadRecipeHandler` |
| `REQUEST_DOWNLOAD_SEQUENCE_RECIPE` | `RequestDownloadSequenceRecipeHandler` |
| `REQUEST_RECIPE_LIST` | `RequestRecipeListHandler` |
| `REQUEST_RECIPE_MODE` | `RequestRecipeModeHandler` |
| `REQUEST_UPLOAD_RECIPE` | `RequestUploadRecipeHandler` |
| `REQUEST_UPLOAD_SEQUENCE_RECIPE` | `RequestUploadSequenceRecipeHandler` |
| `REQUEST_WORK_ORDER` | `RequestWorkOrderHandler` |
| `WORK_ORDER_INFORM` | `WorkOrderInformHandler` |

#### 18.4.3 재시도 정책

설비 송신은 Polly Retry 3회 + Exponential Backoff + Circuit Breaker를 표준으로 한다. 단, 설비 명령이 멱등이 아닌 경우에는 `MessageId`/`TransactionId` 기반 중복 방지 테이블을 먼저 저장한다.

```csharp
public sealed class ResilientEquipmentSender
{
    private readonly ResiliencePipeline _pipeline;
    private readonly IEquipmentCommunicationAdapter _adapter;

    public Task SendAsync(EquipmentMessage message, CancellationToken cancellationToken)
    {
        return _pipeline.ExecuteAsync(
            async token => await _adapter.SendAsync(message, token),
            cancellationToken).AsTask();
    }
}
```

Backoff 기준은 200ms, 500ms, 1s로 시작하고, 설비별 SLA에 따라 조정한다. Timeout은 Connect 3초, Send 5초, Receive Reply 10초를 기본값으로 한다.

#### 18.4.4 인터락 처리 흐름

FDC Spec Out 또는 Alarm Interlock은 다음 순서로 처리한다.

1. 설비 또는 FDC 수집 Handler가 `REPORT_FDC_SPEC_CHECK`, `REPORT_FDC_PARAMETER`, `REPORT_ALARM_STATE`를 수신한다.
2. Adapter가 Raw Message를 `EquipmentMessage`로 만들고 `CorrelationId`, `PlantId`, `EquipmentId`, `TransactionId`를 부여한다.
3. Handler가 ACL을 통해 `FdcInterlockCommand` 또는 `EquipmentAlarmCommand`로 변환한다.
4. `InterlockApplicationService`가 단일 트랜잭션에서 Spec 검증, `FdcTbInterlockHist` 대응 이력, Lot/Equipment Hold 상태 변경, Outbox Event를 저장한다.
5. Commit 이후 Outbox Publisher가 `REQUEST_FDC_INTERLOCK` 또는 `REQUEST_ALARM_INTERLOCK` 설비 명령을 Adapter로 송신한다.
6. SignalR `AlarmHub`가 `plant:{plantId}`, `equipment:{plantId}:{equipmentId}` 그룹에 고우선 메시지를 전송한다.
7. UI는 Interlock Dialog/Alarm Panel을 갱신하고 사용자가 확인한 이력을 감사 로그에 남긴다.

#### 18.4.5 통신 Health Check

| Endpoint | 목적 | Ready 조건 |
|------|------|------|
| `/health/equipment/live` | Adapter Background Loop 생존 확인 | 프로세스와 Worker Loop가 살아 있음 |
| `/health/equipment/ready` | 필수 설비 채널 운영 가능 확인 | Plant별 필수 Adapter 연결, 마지막 Heartbeat 허용 지연 이내 |
| `/health/equipment/{plantId}` | Plant 단위 상태 | Plant 필수 설비의 연결/Heartbeat 요약 |
| `/health/equipment/{plantId}/{equipmentId}` | 단일 설비 상태 | Connect 상태, 마지막 송수신 시각, Circuit 상태, Queue 길이 |

Health 응답에는 `protocol`, `address`, `lastReceivedAt`, `lastSentAt`, `circuitState`, `consecutiveFailureCount`를 포함하고, 민감한 접속 정보와 인증 값은 Masking한다.

### 18.5 SignalR Scale-out 및 Hub 토폴로지

#### 18.5.1 단일 서버와 다중 서버 전략

단일 서버는 SignalR 기본 in-memory Hub Lifetime Manager를 사용한다. 다중 서버 전환 시 Redis Backplane을 활성화한다. 현행 `communication/config.properties`의 Redis 설정은 신규 `ConnectionStrings:Redis`와 `SignalR:Backplane`으로 이관한다.

```csharp
var signalR = builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = 128 * 1024;
});

if (builder.Configuration.GetValue<bool>("SignalR:Backplane:Enabled"))
{
    signalR.AddRedis(builder.Configuration.GetConnectionString("Redis")!);
}
```

#### 18.5.2 Hub 토폴로지

| Hub | 용도 | 주요 메시지 | 우선순위 |
|------|------|------|------|
| `AlarmHub` | Interlock/Alarm/설비 정지성 이벤트 | `FdcInterlockTriggered`, `EquipmentAlarmRaised`, `EquipmentStateChanged` 중 Alarm성 상태 | High |
| `EquipmentHub` | 설비 상태/Heartbeat/상태 Summary | `EquipmentStateChanged`, `ControlStateChanged`, `HeartbeatChanged` | Normal |
| `FdcHub` | FDC Parameter/Trend/Summary | `FdcParameterCollected`, `FdcSummaryUpdated`, `SpecCheckCompleted` | Normal/Low |
| `WorkHub` | Lot/WorkOrder 진행 | `LotTrackedIn`, `LotTrackedOut`, `WorkOrderStarted`, `WorkOrderFinished` | Normal |
| `DeployHub` | WinForms DLL/Manifest 배포 알림 | `ClientVersionChanged`, `DeploymentAvailable` | Low |

Alarm/Interlock은 일반 FDC Trend와 Hub 또는 내부 Channel을 분리한다. Trend 폭주가 Alarm 전달을 지연시키지 않도록 `AlarmHub`는 별도 bounded queue와 작은 Payload를 사용한다.

#### 18.5.3 Hub 그룹 관리

| 그룹명 | 예시 | 가입 기준 | 사용 |
|------|------|------|------|
| Plant | `plant:P01` | 사용자 Plant 권한 | Plant 전체 알림 |
| Equipment | `equipment:P01:EQP001` | 화면이 특정 설비를 구독 | 설비 상태, FDC, Alarm |
| User | `user:kim` | 인증 사용자 ID | 개인 알림, Export 완료 |
| Role | `role:EquipmentEngineer` | 권한/역할 Claim | 운영자 그룹 공지 |
| UI | `ui:EES_FDC_DASHBOARD` | 화면/메뉴 ID | 화면별 데이터 Refresh |

```csharp
public override async Task OnConnectedAsync()
{
    var user = Context.UserIdentifier ?? Context.ConnectionId;
    await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{user}");

    foreach (var plantId in GetAuthorizedPlants(Context.User))
        await Groups.AddToGroupAsync(Context.ConnectionId, $"plant:{plantId}");

    await base.OnConnectedAsync();
}
```

#### 18.5.4 연결 생명주기와 재연결

서버는 `OnConnectedAsync`에서 사용자, Plant, 기본 UI 그룹을 등록하고, 클라이언트가 특정 설비/화면을 열 때 `SubscribeEquipment`, `SubscribeUi` 메서드로 세부 그룹에 가입한다. `OnDisconnectedAsync`에서는 연결 상태와 마지막 오류를 기록한다.

Web 클라이언트는 `WithAutomaticReconnect([0, 2, 10, 30]초)` 정책을 사용한다. WinForms 클라이언트는 재연결 중 UI를 차단하지 않고, 연결 복구 후 현재 화면의 구독 목록을 재전송한다.

#### 18.5.5 메시지 우선순위

SignalR 자체는 메시지 우선순위 큐를 제공하지 않으므로 애플리케이션 레벨에서 분리한다.

| 우선순위 | 채널 | Payload 정책 |
|------|------|------|
| High | `AlarmHub`, `interlock-channel` | 작은 DTO, 즉시 전송, 실패 시 재시도/감사 로그 |
| Normal | `EquipmentHub`, `WorkHub` | 상태 변경 단위 전송, 동일 설비 이벤트 coalescing 허용 |
| Low | `FdcHub`, `DeployHub` | Trend/Summary는 샘플링, 최신값 덮어쓰기 허용 |

### 18.6 DLL 동적 로딩 보안/버전 관리 (.NET 8)

#### 18.6.1 AssemblyLoadContext 기반 격리 로딩

현행 Java 모듈은 `META-INF/MANIFEST.MF`, `BundleActivator`를 가진 OSGi 번들 구조다. .NET 마이그레이션 후 동적 기능 DLL은 `Assembly.LoadFrom` 대신 `AssemblyLoadContext`와 `AssemblyDependencyResolver`로 격리 로딩한다. 플러그인 계약(`NexaMes.Plugin.Abstractions`)은 Default Context에 고정하고, 구현 DLL만 전용 ALC에 적재한다.

```csharp
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginMainAssemblyPath)
        : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginMainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == "NexaMes.Plugin.Abstractions")
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
```

#### 18.6.2 코드 서명 검증

모든 배포 DLL은 X.509 코드 서명 인증서로 서명한다. 로딩 전 인증서 체인, 만료, 신뢰된 Thumbprint, 파일 Hash를 검증한다.

```csharp
public sealed class PluginSignatureVerifier
{
    public void Verify(string assemblyPath, IReadOnlySet<string> allowedThumbprints)
    {
        using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(assemblyPath));
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;

        if (!chain.Build(certificate))
            throw new SecurityException($"Invalid plugin certificate chain: {assemblyPath}");

        if (!allowedThumbprints.Any(x => string.Equals(x, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)))
            throw new SecurityException($"Untrusted plugin signer: {certificate.Thumbprint}");
    }
}
```

운영망에서 온라인 폐기 확인이 불가하면 배포 서버가 서명 검증과 CRL 확인을 선행하고, 클라이언트는 서명된 Manifest의 Hash와 인증서 Thumbprint를 검증한다.

#### 18.6.3 허용 경로 화이트리스트

`App.yml`의 `DLL.Path` 또는 `appsettings`의 `Plugins:AllowedPaths`에 등록된 절대 경로 하위 DLL만 로딩한다. 상대 경로, UNC 임의 경로, 사용자 다운로드 폴더는 차단한다.

```yaml
DLL:
  Path:
    - "C:/ProgramData/NexaMes/Plugins"
    - "C:/Program Files/NexaMes/Plugins"
```

로딩 전 `Path.GetFullPath`로 정규화하고, 심볼릭 링크/재분석 지점은 운영 정책에 따라 차단한다. 파일명만 같은 DLL을 다른 경로에서 주입하는 공격을 막기 위해 Manifest의 `PluginId`, `Version`, `Sha256`, `SignerThumbprint`가 모두 일치해야 한다.

#### 18.6.4 버전 충돌 해결

.NET 8은 .NET Framework의 `app.config bindingRedirect`를 동일하게 사용하지 않는다. 따라서 다음 정책을 적용한다.

| 대상 | 정책 |
|------|------|
| Contract Assembly | `NexaMes.Plugin.Abstractions`는 Host Default Context의 단일 버전만 허용 |
| Plugin Dependency | Plugin별 ALC에 격리하여 서로 다른 dependency version 허용 |
| 공통 라이브러리 | Host가 제공하는 공통 패키지는 `SharedFramework` 목록에 고정 |
| Redirect | Manifest의 `AssemblyRedirects`로 `OldVersionRange -> NewVersion`을 선언하되, 실제 로딩은 ALC Resolver에서만 수행 |
| 호환성 | Major Version 불일치 시 로딩 거부, Minor/Patch는 Contract 호환성 테스트 통과 시 허용 |

#### 18.6.5 DLL 업데이트

운영 중 Hot Swap은 기본 금지한다. 배포 서버에서 새 DLL과 Manifest를 다운로드한 뒤 서명/Hash 검증을 통과하면 staging 폴더에 저장하고, 다음 프로세스 재시작 시 활성화한다.

1. 배포 서버에서 `manifest.json`, DLL, dependency package 다운로드
2. 파일 Hash, X.509 서명, 허용 경로 검증
3. `staging/{pluginId}/{version}`에 저장
4. Host 종료 또는 유지보수 창에서 active pointer 변경
5. 프로세스 재시작 후 새 ALC로 로딩
6. 실패 시 이전 active version으로 롤백

### 18.7 환경별 설정 관리

#### 18.7.1 설정 파일 계층

설정 Override 순서는 다음으로 고정한다.

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. `appsettings.{PlantId}.json` 또는 `Plants:{PlantId}` 섹션
4. 환경변수
5. 운영 Secret Provider(Vault, Kubernetes Secret, Windows Credential Store 등)

현행 설정 파일 이관 기준:

| 현행 파일 | 신규 설정 |
|------|------|
| `Config/Datasource/*-datasource.json` | `ConnectionStrings`, `DatabaseOptions`, Provider별 Pool 옵션 |
| `Config/SO/Datasource.json` | Datasource Metadata Seed 또는 Admin API 관리 데이터 |
| `Config/SO/Tenant.json` | `TenantOptions`, `PlantOptions` |
| `Config/Settings/communication/config.properties` | `CommunicationOptions`, `KafkaOptions`, `RedisOptions`, `SocketOptions`, `WebSocketOptions` |
| `Config/Settings/tool/mail.properties` | `EmailOptions` |
| `Config/Message/websocket-events.xml` | 메시지 파이프라인 옵션. async invoke, transaction begin/commit/rollback 정책 |

비밀번호, DB 계정, Redis 비밀번호, 인증서 비밀번호는 파일에 저장하지 않는다. 개발은 UserSecrets, 운영은 환경변수 또는 Vault를 사용한다.

#### 18.7.2 Secret 관리

| 환경 | Secret Provider | 예시 |
|------|------|------|
| Local/Dev | .NET UserSecrets | `ConnectionStrings:MesDb`, `Redis:Password` |
| Test/Staging | CI/CD Secret + 환경변수 | `NEXAMES_CONNECTIONSTRINGS__MESDB` |
| Production | Vault/KMS/Kubernetes Secret/Windows Credential Store | DB 계정, Kafka SASL, 인증서 PFX Password |

Secret은 `IOptions<T>` 객체에 바인딩되더라도 로그에 출력하지 않는다. Options Debug View, Health 응답, 예외 메시지는 Masking한다.

#### 18.7.3 Plant별 설정

Plant별 DB, 이메일 서버, 업무 시작 시간, 설비 통신 Endpoint를 분리한다.

```json
{
  "Plants": {
    "P01": {
      "ConnectionStringName": "MesDb_P01",
      "BusinessDayStart": "08:30:00",
      "Email": {
        "SmtpProfile": "Default"
      },
      "Equipment": {
        "RequiredChannels": [ "EQP001", "EQP002" ],
        "HeartbeatTimeoutSeconds": 30
      }
    }
  },
  "ConnectionStrings": {
    "MesDb_P01": ""
  }
}
```

Application Service는 `PlantId`를 명시적으로 받아 Plant별 Repository/Connection을 선택한다. `PlantId`가 없는 시스템성 데이터는 `GLOBAL` 컨텍스트로 처리한다.

#### 18.7.4 IOptionsMonitor와 Hot-reload 분류

`IOptionsMonitor<T>`는 운영 중 변경 가능한 값에만 적용한다. DB ConnectionString, DLL 경로, 인증서처럼 프로세스 생명주기와 보안에 영향을 주는 값은 변경 감지만 하고 재시작 필요 상태로 표시한다.

| 설정 | Hot-reload | 처리 |
|------|------|------|
| Rate Limit 수치 | 가능 | 새 요청부터 적용 |
| UI Feature Flag | 가능 | SignalR로 클라이언트 갱신 |
| FDC 동시 처리 제한 | 가능 | `SemaphoreSlim` 교체는 drain 후 적용 |
| Email 발송 From/Template | 가능 | 새 발송부터 적용 |
| 설비 Heartbeat Timeout | 가능 | Health Check 주기 다음부터 적용 |
| DB ConnectionString | 불가 | 변경 감지 후 `/health/ready`에 restart required 표시 |
| Kafka Broker/SASL | 제한 | Consumer 재시작 절차 필요 |
| Redis Backplane | 불가 | SignalR 재시작 필요 |
| DLL Path/Signer Thumbprint | 불가 | 보안 검증 후 프로세스 재시작 필요 |
| Ocelot Route | 제한 | 운영 중 변경은 승인된 배포 절차로만 허용 |

#### 18.7.5 Startup 설정 유효성 검증

Host 시작 시 `ValidateOnStart`와 `IValidateOptions<T>`로 필수 설정을 검증한다. 빈 ConnectionString, 잘못된 URL, 음수 Timeout, 존재하지 않는 DLL Path, Plant별 필수 채널 누락은 즉시 시작 실패로 처리한다.

```csharp
builder.Services
    .AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection("Database"))
    .ValidateDataAnnotations()
    .Validate(options => options.CommandTimeoutSeconds > 0, "DB timeout must be positive.")
    .ValidateOnStart();

builder.Services
    .AddOptions<PlantOptions>()
    .Bind(builder.Configuration.GetSection("Plants"))
    .Validate(options => options.Count > 0, "At least one Plant must be configured.")
    .ValidateOnStart();
```

검증 실패는 `HostAbortedException`으로 숨기지 않고 설정 Key와 원인을 구조화 로그에 남긴다. 단, Secret 값은 절대 출력하지 않는다.

## 19. 기능 보완 설계 (누락 항목)

본 장은 현행 `reference/SmartUX3.5_20260526` 소스에서 기능은 존재하지만 마이그레이션 상세설계에 명시가 부족한 항목을 보완한다. 기준 코드는 `SmartEES/LoginForm.cs`, `RegisterUser.cs`, `ChangePasswordOnLogin.cs`, `MainForm.cs`, `FrameworkSettings.cs`, `Config/Mail/InitPassword(en-US).xml`, WPM Java Rule의 `TrackInLot`, `TrackOutLot`, `MixingLotTrackInOut`, `LotTraceService`이다.

### 19.1 로그인/로그아웃/세션 만료 처리 흐름

#### 19.1.1 현행 코드 기준 동작

현행 `LoginForm`은 시작 시 `GetLanguageListOnLogin`, `GetMessageListOnLogin`, `GetLanguageTypeList`, `GetPlantListOnLogin`을 조회하고, 로그인 시 `MessageWorker("LoginForDotnet")`에 `id`, `Cryptography.SHA256Hash(password)`를 전달한다. 응답의 `USER_STATE`가 `Forgot` 또는 `Create`이면 `ChangePasswordOnLogin`을 먼저 표시하고, 이후 `InitLogin()`에서 `UserInfo.Current`에 사용자, 언어, Plant, IP, 로그인 시간을 채운 뒤 `FrameworkSettings.Initialize()`를 호출한다.

로그인 성공 후에는 `SaveConnectionHistory`에 `UserId`, `ConnectionType=Login`, `ConnectionTime`을 저장하고 반환된 `TXN_HIST_KEY`를 `UserInfo.Current.ConnectionKey`에 보관한다. `MainForm.LinkLogout_Click`은 로그아웃 확인 후 최근 메뉴 저장, `SetLogoutTime()`, 로그인 폼 재표시, 메인 폼 `Dispose()/Close()` 순서로 처리하며, `SetLogoutTime()`은 `SaveConnectionHistory`에 `TxnHistKey`, `UserId`, `ConnectionType=Logout`을 전달한다.

#### 19.1.2 목표 로그인 시퀀스

```text
클라이언트
  -> LoginForm.Show()
  -> LoginForm.LoginValidation()
  -> API POST /auth/login
       요청: loginId, password, plantId, languageType, deviceId
       처리: 사용자/비밀번호/상태/Plant 권한 검증
       처리: SYS_TB_USER.STATE, PASSWORD_STATE, VALID_STATE 검증
       처리: SYS_TB_USER_SESSION 생성
       응답: JWT Access Token, Refresh Token, 사용자 프로필, passwordChangeRequired
  -> JWT 발급
  -> TokenStore.Save(accessToken, refreshToken, expiresAt)
  -> UserInfo.Current 설정
  -> FrameworkSettings.Initialize()
       - NetworkSettings.Default.MessageSettings에 User/Language/Uiid 주입
       - 리소스 수집
       - Dictionary/Message/LanguageType 초기화
  -> SaveConnectionHistory(ConnectionType=Login)
  -> MainForm.Show()
```

마이그레이션 API는 `POST /api/v1/auth/login`을 표준 엔드포인트로 두고, Gateway 호환 경로로 `/auth/login`을 노출한다. 기존 `LoginForDotnet` 룰 호출은 `AuthController.Login()` 내부에서 동일한 검증 흐름으로 대체한다.

#### 19.1.3 인증 토큰 및 세션 모델

Access Token은 8시간, Refresh Token은 7일로 운영한다. Access Token에는 `sub`, `userId`, `plantId`, `languageType`, `sessionId`, `deviceId`, `roles`, `permissionVersion`, `passwordChangedAt` 클레임을 포함한다. Refresh Token은 원문을 DB에 저장하지 않고 SHA-256 또는 HMAC-SHA-256 해시만 저장한다.

신규 테이블은 다음 기준으로 설계한다.

| 테이블 | 주요 컬럼 | 설명 |
|------|------|------|
| `SYS_TB_USER_SESSION` | `SESSION_ID`, `USER_ID`, `DEVICE_ID`, `REFRESH_TOKEN_HASH`, `ACCESS_EXPIRES_AT`, `REFRESH_EXPIRES_AT`, `REVOKED_AT`, `REVOKE_REASON`, `LAST_SEEN_AT`, `IP_ADDRESS`, `USER_AGENT` | 사용자별 로그인 세션 |
| `SYS_TB_USER_LOGIN_HISTORY` | `TXN_HIST_KEY`, `USER_ID`, `SESSION_ID`, `CONNECTION_TYPE`, `CONNECTION_TIME`, `LOGOUT_TIME`, `IP_ADDRESS` | 기존 `SaveConnectionHistory` 대체/확장 |

자동 갱신은 `AuthTokenRefreshHandler`에서 처리한다.

1. API 요청 직전 Access Token 만료까지 5분 이하이면 `POST /api/v1/auth/refresh`를 1회 선갱신한다.
2. 여러 요청이 동시에 갱신하지 않도록 클라이언트 프로세스 단위 `SemaphoreSlim`으로 refresh를 직렬화한다.
3. API 응답이 401이고 해당 요청이 아직 재시도되지 않았으면 refresh 후 원 요청을 1회만 재전송한다.
4. Refresh Token 갱신 시 token rotation을 적용하여 새 refresh token을 발급하고 기존 refresh token은 `ROTATED` 상태로 폐기한다.
5. 이미 폐기된 refresh token이 재사용되면 동일 `SESSION_ID`와 사용자 전체 refresh token family를 즉시 revoke한다.

토큰 저장은 데스크톱 클라이언트 기준으로 Windows DPAPI 또는 Windows Credential Manager를 사용한다. 브라우저 기반 클라이언트는 HttpOnly/Secure/SameSite 쿠키 전략을 사용하고 JavaScript 접근 가능한 저장소에는 refresh token을 저장하지 않는다.

#### 19.1.4 세션 만료 감지 및 처리 정책

서버는 인증 실패 원인을 401 응답 본문에 `code`로 구분한다.

| code | 의미 | 클라이언트 처리 |
|------|------|------|
| `ACCESS_TOKEN_EXPIRED` | Access Token 만료 | refresh 후 원 요청 1회 재시도 |
| `REFRESH_TOKEN_EXPIRED` | Refresh Token 만료 | 재로그인 팝업 또는 강제 로그아웃 |
| `TOKEN_REVOKED` | 로그아웃/관리자 강제 만료 | 강제 로그아웃 |
| `PASSWORD_CHANGED` | 다른 세션에서 비밀번호 변경 | 모든 폼 닫고 재로그인 |
| `SESSION_CONFLICT` | 다중 기기 정책 위반 | 강제 로그아웃 |

정책은 `AuthOptions.ExpiredSessionBehavior`로 분리한다.

| 설정값 | 동작 | 기본값 |
|------|------|------|
| `SilentRefreshThenPrompt` | refresh 실패 시 재로그인 팝업 표시, 성공하면 기존 화면 유지 | 일반 사무용 클라이언트 |
| `ForceLogout` | refresh 실패 또는 401 수신 시 즉시 로그아웃 | MES 현장 단말 기본 |

재로그인 팝업은 `LoginForm`의 언어/Plant 선택 상태를 유지하고 비밀번호만 다시 입력하게 한다. 팝업 성공 시 기존 `UserInfo.Current`와 토큰을 새 값으로 교체하고 열린 화면은 유지한다. 팝업 취소, 2회 실패, 또는 `TOKEN_REVOKED` 계열 오류는 강제 로그아웃으로 전환한다.

#### 19.1.5 로그아웃 처리

로그아웃은 명시적 로그아웃, 세션 만료, 관리자 강제 만료를 같은 종료 루틴으로 처리한다.

```text
MainForm.LinkLogout_Click 또는 SessionExpiredHandler
  -> POST /api/v1/auth/logout 또는 /api/v1/auth/revoke
       body: sessionId, refreshToken, connectionKey
  -> SYS_TB_USER_SESSION.REVOKED_AT 갱신
  -> SaveConnectionHistory(ConnectionType=Logout)
  -> UserInfo.Clear()
  -> TokenStore.Clear()
  -> NetworkSettings.Default.MessageSettings에서 인증 헤더 제거
  -> 열려 있는 MDI/Popup/Modal 폼 전체 닫기
  -> MainForm.Dispose()
  -> LoginForm.Show()
```

현행 `MainForm`은 로그인 폼 `Owner.Show()`와 메인 폼 종료까지 수행하지만 `UserInfo.Current` 초기화가 명시되어 있지 않다. 마이그레이션에서는 `UserInfo.Clear()`를 추가하여 `Id`, `Name`, `Plant`, `LanguageType`, `ConnectionKey`, `LoginTime`, 권한 캐시, 토큰 참조를 모두 제거한다.

#### 19.1.6 다중 기기 세션

동시 로그인 허용 여부는 `AuthOptions.AllowConcurrentSessions`로 설정한다.

| 설정 | 처리 |
|------|------|
| `true` | 사용자별 여러 `SYS_TB_USER_SESSION`을 허용한다. 세션 목록에서 기기, IP, 최근 사용 시각을 확인할 수 있다. |
| `false` | 신규 로그인 성공 시 같은 사용자 기존 세션을 `REVOKED_AT`, `REVOKE_REASON=ReplacedByNewLogin`으로 만료한다. 기존 클라이언트는 다음 API 호출에서 `SESSION_CONFLICT` 401을 받고 강제 로그아웃한다. |

관리자 화면에는 사용자별 활성 세션 조회, 특정 세션 종료, 전체 세션 종료 기능을 제공한다.

### 19.2 비밀번호 정책 상세 설계

#### 19.2.1 현행 코드 기준 동작

`RegisterUser`와 `ChangePasswordOnLogin`은 클라이언트에서 최소 8자, 숫자 1개 이상, 영문자 1개 이상, 특수문자 1개 이상을 Regex로 검증한다. 현행 Regex는 대문자 또는 소문자 중 하나만 있어도 통과할 수 있으므로, 마이그레이션 정책에서는 대문자와 소문자를 각각 1개 이상 요구하도록 강화한다.

`ChangePasswordOnLogin.SaveChangePassword()`는 `USERID`, `CURRENTPASSWORD`, `NEWPASSWORD`, `USERSTATE=Normal`을 `MessageWorker("ChangePasswordOnLogin")`으로 전달한다. `LoginForm.LoginCoreAsync()`는 로그인 결과의 `USER_STATE`가 `Forgot` 또는 `Create`이면 이 폼을 강제로 표시한다.

#### 19.2.2 복잡도 규칙

비밀번호 정책은 서버에서 최종 검증한다. 클라이언트 검증은 사용자 안내용이며 서버 검증 실패 시 400 응답을 반환한다.

| 규칙 | 값 |
|------|------|
| 최소 길이 | 8자 |
| 대문자 | 1개 이상 |
| 소문자 | 1개 이상 |
| 숫자 | 1개 이상 |
| 특수문자 | 1개 이상 |
| 공백 | 앞/뒤 공백 금지, 내부 공백은 `PasswordPolicy.AllowWhitespace`로 제어 |
| 사용자 정보 포함 | `USER_ID`, `USER_NAME`, 이메일 local-part 포함 금지 |

서버 검증 정규식 기본값은 다음과 같다.

```csharp
^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[!@#$%^&*?_~\-\(\)]).{8,}$
```

현행 클라이언트의 `Cryptography.SHA256Hash()` 전송 방식은 호환 기간에만 수용한다. 목표 구조에서는 TLS 위에서 원문 비밀번호를 서버로 전달하고, 서버가 Argon2id 또는 PBKDF2-HMAC-SHA256 기반 salt hash를 생성한다. DB에는 원문, 클라이언트 SHA-256 원문, 복호화 가능한 값을 저장하지 않는다.

#### 19.2.3 만료 정책

비밀번호는 90일 주기로 만료된다. 서버는 로그인 시 `PASSWORD_CHANGED_AT` 또는 `PASSWORD_EXPIRES_AT`을 기준으로 다음 값을 판단한다.

| 조건 | 응답 |
|------|------|
| 만료 7일 전부터 만료 전 | `passwordExpiryWarningDaysLeft`를 반환하고 로그인은 허용 |
| 만료일 초과 | `passwordChangeRequired=true`, `passwordChangeReason=Expired` 반환 |
| `PASSWORD_STATE=Create` 또는 `Forgot` | 최초/초기화 비밀번호 변경 필수 |

클라이언트는 경고 기간에는 `MainForm` 진입 후 1회 경고 메시지를 표시한다. 만료 또는 강제 변경 상태에서는 일반 메뉴를 열기 전에 `ChangePasswordOnLogin`에 해당하는 변경 모달을 표시하고, 성공 전에는 `FrameworkSettings.Initialize()`와 업무 화면 진입을 모두 허용하지 않는다.

#### 19.2.4 이전 비밀번호 재사용 방지

최근 5개의 비밀번호 hash를 `SYS_TB_USER_PASSWORD_HISTORY`에 저장하고 신규 비밀번호가 이력 hash와 일치하면 변경을 거부한다.

| 컬럼 | 설명 |
|------|------|
| `USER_ID` | 사용자 ID |
| `PASSWORD_HISTORY_SEQ` | 사용자별 순번 |
| `PASSWORD_HASH` | salt 포함 hash 또는 hash payload |
| `HASH_ALGORITHM` | `ARGON2ID`, `PBKDF2` 등 |
| `CHANGED_AT` | 변경 시각 |
| `CHANGED_BY` | 변경 주체 |
| `CHANGE_REASON` | `Normal`, `Create`, `Forgot`, `Expired`, `AdminReset` |

비밀번호 변경 트랜잭션은 다음 순서로 처리한다.

1. 사용자 row를 update lock으로 조회한다.
2. 현재 비밀번호 또는 변경용 일회성 토큰을 검증한다.
3. 복잡도 정책을 검증한다.
4. 최근 5개 `SYS_TB_USER_PASSWORD_HISTORY`와 신규 비밀번호를 비교한다.
5. `SYS_TB_USER.PASSWORD_HASH`, `PASSWORD_CHANGED_AT`, `PASSWORD_STATE=Normal`을 갱신한다.
6. 새 hash를 history에 insert하고 5개 초과 이력은 보관 정책에 따라 archive 또는 삭제한다.
7. 기존 refresh token을 모두 revoke하여 다른 기기 세션을 만료한다.

#### 19.2.5 ChangePasswordOnLogin 흐름

`ChangePasswordOnLogin`은 `state='Forgot'` 또는 `state='Create'`일 때 최초 로그인 변경을 강제한다. 현행 코드는 `LoginForm.LoginCoreAsync()`에서 결과 `USER_STATE`를 확인한 뒤 폼을 띄우고, 폼 내부에서 현재 비밀번호, 신규 비밀번호, 확인 비밀번호를 검증한 뒤 `USERSTATE=Normal`로 변경한다.

마이그레이션 흐름은 다음과 같다.

```text
POST /api/v1/auth/login
  -> 사용자 인증 성공
  -> PASSWORD_STATE in ('Forgot', 'Create', 'Expired') 확인
  -> 정상 업무 Access Token 대신 changePasswordToken 발급
  -> 클라이언트 ChangePasswordOnLogin 표시
  -> PATCH /api/v1/users/{userId}/password
  -> 성공 시 PASSWORD_STATE='Normal', STATE='Active'
  -> 정상 Access/Refresh Token 재발급
  -> FrameworkSettings.Initialize()
```

`Forgot` 상태는 `ForgotPassword` 또는 관리자 초기화로 임시 비밀번호가 발급된 경우에 사용한다. `Create` 상태는 사용자 승인 후 최초 임시 비밀번호로 로그인한 경우에 사용한다.

#### 19.2.6 비밀번호 변경 API

```http
PATCH /api/v1/users/{userId}/password
Authorization: Bearer {accessToken 또는 changePasswordToken}
Content-Type: application/json

{
  "currentPassword": "current",
  "newPassword": "new",
  "confirmPassword": "new",
  "reason": "Normal|Create|Forgot|Expired"
}
```

검증 규칙은 다음과 같다.

| 검증 | 오류 코드 |
|------|------|
| `newPassword`와 `confirmPassword` 불일치 | `PASSWORD_NOT_MATCHING` |
| 현재 비밀번호 불일치 | `INVALID_CURRENT_PASSWORD` |
| 복잡도 실패 | `PASSWORD_POLICY_VIOLATION` |
| 최근 5개 이력 재사용 | `PASSWORD_REUSED` |
| 변경 권한 없음 | `PASSWORD_CHANGE_FORBIDDEN` |

응답은 `changedAt`, `nextExpiresAt`, `passwordState`, `sessionRevokedCount`를 반환한다. 성공 후 클라이언트는 `TokenStore`를 갱신하고 `UserInfo.Current`의 인증 상태를 재설정한다.

### 19.3 사용자 등록/승인 흐름 (전체 설계)

#### 19.3.1 현행 RegisterUser 코드 기준 동작

현행 `RegisterUser`는 로그인 화면의 `linkRequest`에서 열리며, `LanguageType`, `dtDictionary`, `dtMessage`를 전달받아 다국어 라벨과 메시지를 구성한다. 중복 확인은 `CheckDuplicateIdOnLogin` 쿼리로 `SYS_TB_USER.USER_ID` 존재 여부를 확인한다.

`SaveUserData()`는 다음 값을 `MessageWorker("RegisterUser")`에 `list`로 전달한다.

| 현행 컬럼 | 화면 입력 |
|------|------|
| `USER_ID` | `txtUserId` |
| `USER_NAME` | `txtUserName` |
| `PASSWORD` | `Cryptography.SHA256Hash(txtPassword.Text)` |
| `NICKNAME` | `txtNickName` |
| `DESCRIPTION` | `txtDescription` |
| `DEPARTMENT` | `txtDepartment` |
| `POSITION` | `txtPosition` |
| `DUTY` | `txtDuty` |
| `EMAIL_ADDRESS` | `txtEmailAddress` |
| `HOME_ADDRESS` | `txtHomeAddress` |
| `CELL_PHONE_NUMBER` | `txtCellphoneNumber` |
| `DEFAULT_LANGUAGE_TYPE` | `cboDefaultLanguageType` |
| `VALID_STATE` | `InValid` |
| `USER_STATE` | `Request` |
| `_STATE_` | `added` |

마이그레이션에서는 이 현행 계약을 기준으로 하되, 승인형 가입 프로세스에 맞춰 Plant와 약관 동의를 필수 입력으로 추가한다. 신규 플로우에서는 신청 단계에서 비밀번호를 활성 계정 비밀번호로 사용하지 않고, 승인 시 임시 비밀번호를 생성한다.

#### 19.3.2 사용 신청 폼(RegisterUserForm)

`RegisterUserForm`의 필수 입력은 다음과 같다.

| 항목 | 필수 | 설명 |
|------|------|------|
| 아이디 | Y | `CheckDuplicateIdOnLogin` 또는 `GET /api/v1/users/exists/{userId}`로 중복 확인 |
| 이름 | Y | `USER_NAME` |
| 이메일 | Y | 관리자 승인/반려/임시 비밀번호 발송 대상 |
| 부서 | Y | `DEPARTMENT` |
| 직급/직위 | Y | `POSITION` |
| 직책 | N | 현행 `DUTY` 유지 |
| 플랜트 | Y | `GetPlantListOnLogin` 기준 선택, 승인 후 사용자-Plant 권한 매핑 생성 |
| 기본 언어 | Y | 현행 `cboDefaultLanguageType` 유지 |
| 휴대폰/주소/설명/별명 | N | 현행 선택 입력 유지 |
| 약관 동의 | Y | 개인정보 처리 및 시스템 사용 약관 동의 시각/IP 저장 |

신청 저장 전 클라이언트는 필수 입력, 이메일 형식, 약관 동의, 아이디 중복 확인 여부를 검증한다. 서버는 동일 검증을 재수행한다.

#### 19.3.3 신청 저장 API

```http
POST /api/v1/users/request
Content-Type: application/json

{
  "userId": "operator01",
  "userName": "홍길동",
  "email": "operator01@example.com",
  "department": "Production",
  "position": "Engineer",
  "duty": "Shift A",
  "plantId": "P01",
  "defaultLanguageType": "ko-KR",
  "cellPhoneNumber": "010-0000-0000",
  "termsAccepted": true
}
```

저장 처리 기준은 다음과 같다.

1. `SYS_TB_USER.USER_ID` 중복을 확인한다. 기존 row가 `STATE=Reject`이면 재신청으로 전환할 수 있다.
2. `SYS_TB_USER`에 `STATE='Request'`, `VALID_STATE='InValid'`로 저장한다.
3. `SYS_TB_USER_REQUEST`에 신청 Plant, 약관 버전, 동의 시각, 동의 IP, 신청 사유를 저장한다.
4. 관리자 그룹에 이메일 알림을 발송한다.
5. 감사 로그에 `UserRequestCreated`를 기록한다.

현행 `USER_STATE='Request'` 값은 신규 `STATE='Request'`와 매핑한다. 단계적 전환 기간에는 두 컬럼을 병행 갱신하여 기존 쿼리와 신규 API가 모두 동작하게 한다.

#### 19.3.4 관리자 승인 화면(UserRequestApproval)

`UserRequestApproval` 화면은 관리자 메뉴에 추가한다.

| 영역 | 기능 |
|------|------|
| 검색 조건 | Plant, 신청일, 상태, 사용자 ID/이름/이메일 |
| 신청 목록 | `Request`, `Reject`, `Active` 상태 조회 |
| 상세 패널 | 신청 정보, 약관 동의 정보, Plant 권한, 반려 사유 |
| 액션 | 승인, 반려, 재발송, 이력 보기 |

API는 다음과 같이 구성한다.

| 메서드 | 경로 | 설명 |
|------|------|------|
| `GET` | `/api/v1/users/requests` | 신청 목록 조회 |
| `GET` | `/api/v1/users/requests/{requestId}` | 신청 상세 조회 |
| `PATCH` | `/api/v1/users/requests/{requestId}/approve` | 승인 |
| `PATCH` | `/api/v1/users/requests/{requestId}/reject` | 반려 |

#### 19.3.5 승인 처리

승인 트랜잭션은 다음 순서로 수행한다.

1. 신청 row가 `STATE='Request'`인지 확인하고 update lock을 획득한다.
2. `SYS_TB_USER.STATE='Active'`, `VALID_STATE='Valid'`, `PASSWORD_STATE='Create'`로 갱신한다.
3. 임시 비밀번호를 서버에서 생성하고 hash 저장 및 `SYS_TB_USER_PASSWORD_HISTORY`에 기록한다.
4. 신청 Plant를 기준으로 사용자-Plant 권한 및 기본 권한 그룹을 생성한다.
5. `Config/Mail/InitPassword(en-US).xml` 계열 템플릿을 사용해 임시 비밀번호를 발송한다. 템플릿의 `"${PASSWORD}"` 자리에는 생성한 임시 비밀번호를 치환한다.
6. 신청자에게 승인 이메일, 관리자에게 처리 완료 알림을 발송한다.
7. `UserRequestApproved` 감사 로그를 남긴다.

승인 후 최초 로그인에서는 `PASSWORD_STATE='Create'`로 인해 `ChangePasswordOnLogin` 흐름이 강제된다. 기존 단일 `USER_STATE`만 사용하는 호환 기간에는 승인 시 `USER_STATE='Create'`, 신규 `STATE='Active'`를 함께 저장한다.

#### 19.3.6 반려 처리

반려 처리에는 반려 사유 입력을 필수로 한다.

1. 신청 row가 `STATE='Request'`인지 확인한다.
2. `SYS_TB_USER.STATE='Reject'`, `VALID_STATE='InValid'`로 갱신한다.
3. `SYS_TB_USER_REQUEST.REJECT_REASON`, `REJECTED_BY`, `REJECTED_AT`을 저장한다.
4. 신청자 이메일로 반려 사유를 발송한다.
5. `UserRequestRejected` 감사 로그를 남긴다.

반려된 사용자는 같은 ID로 재신청할 수 있다. 재신청 시 기존 row를 새 `Request`로 되돌릴지, 신규 request version을 생성할지는 `UserRegistrationOptions.ReapplyMode`로 설정한다. 기본값은 이력 보존을 위해 request version 증가이다.

#### 19.3.7 상태 전이

```text
Request
  -> Active  : 관리자 승인, 임시 비밀번호 발급, PASSWORD_STATE=Create
  -> Reject  : 관리자 반려, 반려 사유 필수

Reject
  -> Request : 재신청 가능

Active
  -> Active  : 최초 로그인 비밀번호 변경 후 PASSWORD_STATE=Normal
```

상태 전이는 API 서버에서만 수행하며 클라이언트는 상태 값을 직접 갱신하지 않는다.

구현은 본 절을 다음과 같이 적응한다. 신청 생명주기는 `SYS_USER_REQUEST` 테이블(V015, USER_ID UNIQUE)이 단독 소유하고, 설계 원문의 `SYS_TB_USER.STATE/VALID_STATE` 컬럼은 추가하지 않는다 — `SYS_USER` 행은 승인 시점에만 생성되므로 미승인 신청자는 사용자 행 자체가 없어 로그인이 자연 차단되고, 본 절의 `Active`는 별도 상태 값이 아니라 사용자 행의 존재로 표현된다(신청 상태는 `Request/Approved/Rejected` 3값). 재신청은 새 행이 아니라 같은 행을 `Rejected -> Request`로 전환하며 `REQUEST_VERSION`을 1 올리고, 직전 반려 이력(사유/반려자/시각)은 보존한다 — 별도 ReapplyMode는 도입하지 않는다. 신청 시각은 `REQUESTED_AT` 명시 컬럼으로 기록하며 재신청 시 갱신된다(감사 컬럼 CreatedAt은 DB 복원 시 보존되지 않는 구현 제약). 이메일 검증은 '@' 포함 여부로 단순화하고, 언어 문자열은 `Enum.TryParse` + `Enum.IsDefined`를 통과하지 못하면 KoKr로 폴백한다 — TryParse만으로는 숫자 문자열이 미정의 enum 값으로 통과하기 때문이다.

승인은 설계 원문의 Plant 권한 그룹 매핑 대신 승인 화면에서 역할(RoleId)을 지정하는 방식으로 적응한다(공백이면 기본 "USER", 신청서의 PlantId는 그대로 보존). 승인 시 임시 비밀번호를 서버에서 생성·해시 저장(`PasswordState=Create`, `User.IssueInitialPassword`)하고 현행 `InitPassword({culture}).xml` 메일 템플릿을 재사용해 승인 안내와 임시 비밀번호를 한 통으로 발송한다 — 메일 발송 실패는 로그만 남기고 승인을 되돌리지 않는다. UnitOfWork가 없으므로 전수 검증 후 사용자 생성 -> 신청 갱신 순서로 기록하며, 신청 갱신이 실패해 상태가 어긋나면 재승인 시 사용자 중복 충돌로 드러난다. 신청 접수 시 관리자 알림 메일은 `Registration:AdminEmail` 설정이 있을 때만 발송하고, 감사 추적(UserRequestCreated/Approved/Rejected)은 별도 테이블 대신 §17.3의 Serilog 구조화 로그로 남긴다.

API는 `GET /api/v1/users/exists/{userId}`(중복확인)와 `POST /api/v1/users/request`(신청)를 익명으로 열되 §18.2.3의 "auth" 정책(IP당 10req/min)으로 ID 열거와 신청 폭주를 완화하고, 목록/승인/반려는 ADMIN 전용이다. 약관 동의 시각과 IP는 서버가 기록한다. 등록 화면(`Register.razor`)의 Plant는 익명 상태라 마스터 조회가 불가해 텍스트 입력으로 적응하고, [중복확인]을 통과한 ID만 제출할 수 있다(ID 변경 시 재확인 요구). 컨트롤러 액션명은 `ControllerBase.Request` 속성과의 CS0108 충돌을 피해 `SubmitRequest`로 한다(라우트는 `request` 유지).

### 19.4 Lot TrackIn/TrackOut 생산 추적 기능 설계

#### 19.4.1 현행 Java Rule 기준 동작

현행 생산 추적은 다음 Java Rule과 Service가 담당한다.

| 현행 Rule/Service | 주요 동작 | C# 마이그레이션 대상 |
|------|------|------|
| `s-rule-factory.wpm/rule/TrackInLot` | `lotList` 또는 `lotInfo`를 받아 Lot TrackIn, 설비 TrackIn, W/O Start, 자재 Lot consume 처리 | `LotTrackingService.TrackInAsync()` |
| `s-rule-factory.wpm/rule/TrackOutLot` | 불량 처리 후 Lot TrackOut, 설비 TrackOut, Dispatch, W/O Finish, 반제품 MaterialLot 생성 | `LotTrackingService.TrackOutAsync()` |
| `s-rule-factory.poc/rule/MixingLotTrackInOut` | Mixing Lot의 투입량 합산 후 TrackIn과 TrackOut을 연속 처리 | `LotTrackingService.MixingTrackInOutAsync()` |
| `LotTraceService` | TrackIn/TrackOut/Finish 이력 생성 | `LotHistoryWriter` |

`LotParameter.TrackInLotSet`은 `PLANT_ID`, `LOT_ID`, `EQUIPMENT_ID`, `RECIPE_DEF_ID`, `RECIPE_DEF_VERSION`, `TRACK_IN_USER`, `TRACK_IN_TIME`을 사용한다. `TrackOutLotSet`은 `PLANT_ID`, `LOT_ID`, `EQUIPMENT_ID`, `CARRIER_ID`, `TRACK_OUT_USER`, `TRACK_OUT_TIME`을 사용한다.

#### 19.4.2 C# 서비스 구조

```csharp
public interface ILotTrackingService
{
    Task<TrackInResult> TrackInAsync(TrackInCommand command, CancellationToken ct);
    Task<TrackOutResult> TrackOutAsync(TrackOutCommand command, CancellationToken ct);
    Task<MixingTrackResult> MixingTrackInOutAsync(MixingTrackCommand command, CancellationToken ct);
}
```

구현 클래스는 `LotTrackingService`로 두고 다음 의존성을 주입한다.

| 의존성 | 역할 |
|------|------|
| `ILotRepository` | `WpmTbLotData` 대체, Lot 상태/공정/수량 조회 및 저장 |
| `IEquipmentRepository` | 설비 TrackIn/TrackOut count 및 설비 상태 저장 |
| `IWorkOrderRepository` | 작업지시 Confirm/Start/Finish 상태 전이 |
| `IRecipeMappingService` | 공정/설비/품목 기준 Recipe 자동 매핑 및 검증 |
| `ILotHistoryWriter` | `STD_TB_LOT_HISTORY` 기록 |
| `IMaterialLotService` | 투입 자재 consume, 반제품 material lot 생성 |
| `IUnitOfWork` | Lot/Equipment/WorkOrder/History 원자성 보장 |

API는 다음 기준으로 제공한다.

| 메서드 | 경로 | 설명 |
|------|------|------|
| `POST` | `/api/v1/lots/{lotId}/track-in` | Lot TrackIn |
| `POST` | `/api/v1/lots/{lotId}/track-out` | Lot TrackOut |
| `POST` | `/api/v1/lots/mixing/track-in-out` | Mixing Lot TrackIn/TrackOut 일괄 처리 |
| `GET` | `/api/v1/lots/{lotId}/route` | Lot 경로 및 이력 조회 |
| `GET` | `/api/v1/reports/lot-tracking` | 생산 추적 보고서 |

#### 19.4.3 Lot 상태 전이

문서 표준 상태는 다음과 같이 정의한다.

```text
Created -> Queued -> TrackIn(설비+공정) -> Processing -> TrackOut -> Completed
```

현행 상수와의 매핑은 다음과 같다.

| 표준 상태 | 현행 Java 상수/컬럼 | 설명 |
|------|------|------|
| `Created` | `LOTSTATE_CREATED` | Lot 생성 직후 |
| `Queued` | `LOTSTATE_INPRODUCTION` + `LOTPROCESSSTATE_READY` 또는 `Idle` | 공정 대기 |
| `TrackIn` | `TRANSITIONID_TRACKIN` | 설비/레시피/작업자/시간 설정 |
| `Processing` | `LOTPROCESSSTATE_RUN` | TrackIn 후 생산 진행 |
| `TrackOut` | `TRANSITIONID_TRACKOUT` | 설비/레시피 반납, TrackOut 시간 설정 |
| `Completed` | `LOTSTATE_FINISHED` | Dispatch 결과 마지막 공정 종료 |

서비스는 상태 전이를 직접 문자열 변경으로 처리하지 않고 `LotStateMachine`을 통해 허용된 transition만 수행한다. Hold 상태(`IS_HOLD='Y'`)인 Lot은 TrackIn/TrackOut을 거부한다.

#### 19.4.4 TrackIn 화면 및 API

TrackIn 화면은 현장 작업자가 바코드 스캔 또는 수동 입력으로 Lot을 선택하는 화면이다.

| 입력 | 설명 |
|------|------|
| Lot ID | 바코드 스캔 기본, 수동 입력 허용 |
| Plant | 로그인 Plant 기본값 |
| 설비 | 설비 팝업/스캔 선택, 설비 권한 및 상태 검증 |
| 공정 | Lot의 현재 `SegmentId`, `ProcessPathStack`에서 자동 표시 |
| Recipe | `IRecipeMappingService`로 설비+공정+품목 기준 자동 매핑 |
| 작업자 | `UserInfo.Current.Id` |

TrackIn 요청 DTO는 다음과 같다.

```json
{
  "plantId": "P01",
  "lotId": "LOT202606090001",
  "equipmentId": "EQP001",
  "recipeDefId": "RCP-A",
  "recipeDefVersion": "001",
  "trackInUser": "operator01",
  "materialLotList": [
    {
      "materialLotId": "MAT001",
      "consumedQty": 10.0
    }
  ]
}
```

TrackIn 검증은 다음 순서로 수행한다.

1. Lot 존재 및 Plant 일치 확인
2. Lot 상태가 `Queued` 또는 현행 `LOTSTATE_INPRODUCTION`인지 확인
3. Hold 상태가 아닌지 확인
4. 설비가 입력된 경우 설비 존재, Plant 일치, 사용 가능 상태 확인
5. 공정이 TrackIn 필요 공정인지 확인
6. Recipe 자동 매핑 결과와 입력 recipe가 일치하는지 확인
7. 자재 투입 목록이 있으면 BOM/투입 가능 수량/자재 상태 확인
8. `TRACK_IN_TIME`은 서버 시각으로 설정

현행 `TrackInLot`은 `TrackInEquipmentService.setIsUseValidationRecipe(false)`로 설비 recipe 검증을 끈다. 마이그레이션 기본값은 recipe 검증 사용이며, 기존 현장 예외를 위해 `LotTrackingOptions.SkipRecipeValidationForLegacyEquipment`를 Plant/설비별로 둘 수 있다.

TrackIn 성공 시 `WpmTbLotData` 대체 row에 설비, recipe, track-in 사용자/시각을 저장하고 process state를 `Processing`으로 전이한다. 설비의 TrackIn count를 갱신하고, 작업지시가 `Confirm`이면 `Start`로 전이한다.

#### 19.4.5 TrackOut 화면 및 API

TrackOut 화면은 생산수량, 불량수량, 다음 공정 이동을 처리한다.

| 입력 | 설명 |
|------|------|
| Lot ID | TrackIn 상태 Lot |
| 설비 | TrackIn 설비와 일치해야 함 |
| 생산수량 | 기본값은 현재 Lot Qty, 부분 처리 시 수정 |
| 불량수량 | `defectList`로 불량 코드별 입력 |
| Carrier | 필요 시 `CARRIER_ID` 입력 |
| 다음 공정 | Dispatch 결과 자동 선택, 수동 변경은 권한 필요 |

TrackOut 요청 DTO는 다음과 같다.

```json
{
  "plantId": "P01",
  "lotId": "LOT202606090001",
  "equipmentId": "EQP001",
  "carrierId": "CARR001",
  "trackOutUser": "operator01",
  "qty": 100.0,
  "defectList": [
    {
      "defectCode": "D001",
      "defectQty": 2.0
    }
  ]
}
```

TrackOut 검증은 다음 순서로 수행한다.

1. Lot 존재 및 Plant 일치 확인
2. Lot 상태가 `Processing`인지 확인
3. Segment의 `IS_TRACK_IN_REQUIRED='Y'`이면 현재 process state가 `Run`이고 TrackIn 설비와 요청 설비가 일치하는지 확인
4. Hold 상태가 아닌지 확인
5. 생산수량과 불량수량이 음수가 아니고 Lot Qty 범위 내인지 확인
6. 불량 코드는 유효한 reason/defect code인지 확인
7. `TRACK_OUT_TIME`은 서버 시각으로 설정

현행 `TrackOutLotService`는 TrackOut 시 Lot의 `EquipmentId`, `RecipeDefId`, `RecipeDefVersion`을 null로 설정한다. 마이그레이션에서도 TrackOut 성공 시 설비 점유와 recipe 점유를 반납한다.

TrackOut 성공 후 `DispatchLotService.dispatchLotWithRework`에 해당하는 다음 공정 이동을 수행한다. 마지막 공정이면 Lot을 `Completed`로 전이하고, 같은 작업지시의 진행 중 Lot이 없으면 WorkOrder를 Finish 처리한다. 품목 유형이 현행 `ITEMTYPE_HALB`인 경우 반제품 material lot 생성 로직을 실행한다.

#### 19.4.6 Lot 이력 테이블

현행 `LotTraceService`는 `WpmTbLotTraceData`에 `WorkOrderId`, `ItemDefId`, `ProcessPathId`, `SegmentId`, `EquipmentId`, `ProcessState`, `LotState`, `ExecutionId`, `ExecutionUser`, `ExecutionTime`, `Qty`, `DefectQty`를 기록한다. 마이그레이션 표준 테이블은 `STD_TB_LOT_HISTORY`로 정의한다.

| 컬럼 | 설명 |
|------|------|
| `LotHistoryId` | PK |
| `PlantId` | Plant |
| `LotId` | Lot ID |
| `EquipmentId` | 설비 |
| `ProcessId` | 공정 또는 Segment ID |
| `ProcessPathId` | 공정 경로 |
| `RecipeDefId` | Recipe ID |
| `RecipeDefVersion` | Recipe Version |
| `TrackInTime` | TrackIn 시각 |
| `TrackOutTime` | TrackOut 시각 |
| `ExecutionId` | `TrackIn`, `TrackOut`, `Finish` |
| `ExecutionUser` | 처리자 |
| `Qty` | 생산/현재 수량 |
| `DefectQty` | 불량 수량 |
| `LotState` | 처리 후 Lot 상태 |
| `ProcessState` | 처리 후 Process 상태 |
| `TxnHistKey` | 트랜잭션 이력 키 |
| `CreatedAt` | 기록 시각 |

조회 성능을 위해 `(PlantId, LotId, TrackInTime)`, `(PlantId, EquipmentId, TrackInTime, TrackOutTime)`, `(PlantId, ProcessId, TrackInTime)` 인덱스를 생성한다.

#### 19.4.7 MixingLot 설계

현행 `MixingLotTrackInOut`은 `WpmTbMixingLot`에서 같은 `PlantId`, `LotId`의 입력 row를 조회해 `InQty` 합계를 Lot Qty로 반영한 뒤 TrackIn과 TrackOut을 연속 수행한다. `ConsumeMixingMaterialLot`은 BOM 기준 투입 수량을 검증하고 `WpmTbConsumeMaterialLot`, `WpmTbMixingLot`을 기록한 뒤 material lot을 consume한다.

마이그레이션 `MixingTrackInOutAsync()`는 다음 흐름으로 처리한다.

```text
입력 Lot/MaterialLot 목록 검증
  -> BOM 및 MixingRate, SetMinQty, SetMaxQty 검증
  -> 출력 Lot 생성 또는 기존 Mixing Lot 조회
  -> 입력 Lot/MaterialLot 상태를 Consumed로 전이
  -> 출력 Lot Qty = 입력 InQty 합계
  -> 출력 Lot TrackIn
  -> 출력 Lot TrackOut
  -> STD_TB_LOT_HISTORY에 입력/출력 관계 기록
```

여러 Lot을 하나의 출력 Lot으로 통합할 때는 `STD_TB_LOT_MIXING_RELATION`을 추가한다.

| 컬럼 | 설명 |
|------|------|
| `PlantId` | Plant |
| `OutputLotId` | 통합 결과 Lot |
| `InputLotId` | 입력 Lot |
| `InputMaterialLotId` | 입력 Material Lot |
| `InputQty` | 투입량 |
| `MixingRate` | 배합률 |
| `ConsumedAt` | 소비 시각 |
| `ConsumedBy` | 소비 사용자 |

입력 Lot의 상태는 `Consumed`로 변경한다. 현행 상수에는 MaterialLot 기준 `MATERIALLOTSTATE_CONSUMED='Consumed'`가 존재하므로, Lot과 MaterialLot을 모두 다루는 경우 Lot 상태와 MaterialLot 상태를 분리하여 저장한다.

#### 19.4.8 생산 추적 보고서

생산 추적 보고서는 `STD_TB_LOT_HISTORY`, `STD_TB_LOT_MIXING_RELATION`, 불량 이력 테이블을 조인하여 제공한다.

| 보고서 | 주요 내용 |
|------|------|
| Lot 경로 조회 | Lot별 공정 순서, TrackIn/TrackOut 시각, 설비, 작업자, Recipe |
| 설비별 체류 시간 | `TrackOutTime - TrackInTime`, 설비/공정/Plant별 평균/최대/분포 |
| 불량 이력 연계 | TrackOut 시점 defect code, defect qty, 수리/재작업 여부 |
| Mixing 추적 | 출력 Lot 기준 입력 Lot/MaterialLot, 투입량, 배합률, 소비 시각 |

보고서 API는 `GET /api/v1/reports/lot-tracking`으로 제공하고, 조건은 `plantId`, `lotId`, `equipmentId`, `processId`, `from`, `to`, `includeDefect`, `includeMixing`을 지원한다. 대용량 조회는 커서 기반 페이지네이션과 CSV export 작업 큐를 사용한다.

구현은 본 절을 다음과 같이 적응한다. 테이블명은 구현 DB 네이밍 규약에 따라 `PPM_LOT`/`PPM_LOT_HISTORY`/`PPM_LOT_MIXING_RELATION`(V014)으로 하고, 공정 경로(RouteSteps)는 별도 라우팅 마스터 없이 `>` 구분 문자열로 직렬화해 Lot 행에 보관한다. Lot은 생성 즉시 `Queued` 상태로 시작하며, 현행 materialLotList(자재 Lot 소비) 파라미터는 자재 모듈 미구현으로 이번 범위에서 제외한다 — Mixing 추적의 입력은 Lot 간 관계(`PPM_LOT_MIXING_RELATION`)로만 기록한다. 설비/Recipe/불량 코드 마스터 접근은 `ITrackingMasterGateway` 포트로 분리해 PPM 모듈이 MDM/RMS/QMS 구현에 직접 의존하지 않게 한다.

TrackIn 검증은 Lot 존재/Plant 일치 -> Hold/상태 -> 설비(존재·Plant 일치·사용 가능) -> Recipe 순서로 수행하고, Recipe는 배포(Released) 상태이면서 설비 클래스가 일치하고 버전이 맞아야 통과한다 — Recipe 미지정은 현행 `setIsUseValidationRecipe(false)` 설비의 적응으로 허용한다. 모든 공정은 TrackIn을 거쳐야 TrackOut할 수 있고(`LotStateMachine` 상태 기계), TRACK_IN/OUT 시각은 항상 서버 시각(UTC)이다. UnitOfWork가 없으므로 입력 전수 검증을 모두 통과한 뒤에만 상태를 변경하며, 작업지시 자동 시작(Issued -> Start)과 마지막 Lot 완료 시 자동 마감(Finish)은 best-effort로 처리해 실패해도 TrackIn/TrackOut 자체는 유지하고 화면에서 수동 처리할 수 있다. TrackOut 이력은 점유 해제 전에 캡처한 설비/Recipe/공정으로 기록한다 — TrackOut이 설비 점유를 비우기 때문이다.

생산 추적 보고서는 커서 기반 페이지네이션과 CSV export 작업 큐 대신 행 수 상한으로 적응한다(`maxRows` 기본 1000, 최대 5000) — 초과분은 기간/설비/공정 조건을 좁혀 재조회한다. `includeDefect`/`includeMixing` 합성 조회는 Lot 경로 조회(`GET /api/v1/lots/{lotId}/route`)가 이력과 Mixing 관계를 함께 반환하는 것으로 대체한다. 현행 TxnHistKey는 §17.3의 CorrelationId가 같은 역할을 하므로 도입하지 않고, 상태 enum은 전역 `JsonStringEnumConverter`로 문자열 직렬화해 화면 DTO와 계약을 일치시킨다.

## 20. 기능 보완 설계 (부족 항목)

본 장은 `reference/SmartUX3.5_20260526`의 현행 구현을 기준으로 기능 부족 항목을 보완하기 위한 상세 설계이다. 주요 근거 코드는 `SmartBaseForm`, `SmartConditionBaseForm`, `SmartBandedGrid`, `MenuRepository`, `FormCreator`, `MainForm`, `EquipmentAlarmHistory`, `EquipmentStateChange`, `SpecValidationService`, `REQUEST_FDC_INTERLOCK`, `REQUEST_DOWNLOAD_RECIPE`이다. 현행 WinForms 구현의 동작 의미는 유지하고, C# 마이그레이션에서는 API, SignalR, 캐시, 비동기 처리, 감사 로그를 명시적으로 분리한다.

### 20.1 메뉴 동적 로딩 및 권한별 필터링 흐름

현행 `MenuRepository.InitMenu()`는 로그인 사용자의 `UIID`로 `GetMenuList` 쿼리를 실행하고, `MainForm.InitializeMenuBar()`가 `PARENTMENUID`, `DISPLAYSEQUENCE`, `MENUTYPE`, `VALIDSTATE` 기준으로 메뉴바를 구성한다. `GetMenuList`는 `SYS_TB_MENU`에 `SYS_TB_MENU_AUTHORITY`, `SYS_TB_AUTHORITY_USER` 권한 조건을 적용하므로, 마이그레이션 후에도 이 권한 필터를 `MenuRepository.GetAuthorizedMenus()`의 단일 진입점으로 유지한다.

`SYS_TB_MENU` 계층 구조는 DB 원본 컬럼 `PARENT_MENU_ID`, `DISPLAY_SEQUENCE`를 각각 클라이언트 모델의 `PARENTMENUID`, `SEQUENCE`로 매핑한다. `DEPTH`가 DB에서 제공되지 않는 환경은 `PARENTMENUID`를 따라 루트부터 계산한다.

```sql
SELECT
    M.UI_ID,
    M.MENU_ID,
    M.MENU_NAME,
    M.PARENT_MENU_ID,
    M.DISPLAY_SEQUENCE,
    M.MENU_TYPE,
    M.PROGRAM_ID,
    M.OPTIONS,
    M.VALID_STATE,
    M.IMAGEID
FROM SYS_TB_MENU M
JOIN SYS_TB_MENU_AUTHORITY MA
  ON MA.UI_ID = M.UI_ID
 AND MA.MENU_ID = M.MENU_ID
 AND MA.VALID_STATE = 'Valid'
JOIN SYS_TB_AUTHORITY_USER AU
  ON AU.AUTHORITY_ID = MA.AUTHORITY_ID
 AND AU.USER_ID = @userId
 AND AU.VALID_STATE = 'Valid'
WHERE M.VALID_STATE = 'Valid'
  AND M.UI_ID IN ('SYS', @uiId)
ORDER BY M.PARENT_MENU_ID, M.DISPLAY_SEQUENCE, M.MENU_TYPE, M.MENU_ID;
```

트리 렌더링 알고리즘은 다음 순서로 처리한다.

1. `GetAuthorizedMenus(uiId, userId)` 결과를 `MenuNode` 목록으로 변환한다.
2. `PARENTMENUID`가 `null`, 빈 문자열, `*`이면 루트 노드로 정규화한다.
3. `MENUID` 기준 딕셔너리를 만들고, 각 노드를 부모의 `Children`에 연결한다.
4. 부모가 권한 필터로 제거된 고아 노드는 루트에 붙이되 `IsOrphan = true`로 표시하고 로그를 남긴다.
5. 형제 노드는 `SEQUENCE`, `MENUTYPE(Folder 우선)`, `MENUID` 순으로 정렬한다.
6. `DEPTH`는 루트 0부터 DFS로 계산하고, 최대 깊이는 기존 `MainForm.InitializeMenuList()`의 3단 메뉴 표현을 기준으로 제한한다.
7. `MENUTYPE = Screen`만 클릭 가능 메뉴로 렌더링하고, `Folder`는 컨테이너로만 사용한다.

메뉴 캐시는 로그인 성공 시 전체 권한 메뉴를 1회 로드하여 `MenuCache`에 저장한다. 캐시 키는 `uiId:userId:languageType`이며, 값은 원본 목록과 트리 모델을 모두 가진다. 권한 변경 이벤트 또는 관리자 권한 저장 후 `MenuPermissionChanged(userId)` 이벤트가 발생하면 해당 사용자 캐시를 무효화하고, 현재 로그인 사용자와 일치할 때 `MenuRepository.ReloadAuthorizedMenus()`를 호출한다. 재로드 중에는 기존 메뉴를 유지하고, 성공 시점에만 UI 트리를 교체한다.

메뉴 아이콘은 `IMAGEID` 컬럼을 우선 사용한다. 현행 `ResourceCollector.GetImage(name)` 구조를 유지하여 `IMAGEID` 값으로 리소스를 조회하고, 과거 메뉴의 `OPTIONS` JSON에 `Image` 값이 있는 경우 보조 키로 사용한다. 이미지가 없으면 기본 화면 아이콘을 표시하고, 리소스 조회 실패는 메뉴 로딩 실패로 처리하지 않는다.

메뉴 로딩 실패 시에는 빈 메뉴 트리와 `연결 재시도` 버튼을 표시한다. 이 버튼은 마지막 로그인 컨텍스트로 `GetAuthorizedMenus()`를 재호출하며, 실패 사유는 `MenuLoadFailure` 로그와 사용자 알림에 분리 기록한다. 업무 화면은 열지 않고, 즐겨찾기와 최근 메뉴도 권한 메뉴가 정상 로드된 후에만 렌더링한다.

### 20.2 폼 생명주기 상세 설계

현행 폼 열기 경로는 `MainForm.MenuItem_Click()` -> `MenuRepository.OpenMenu()` -> `FormCreator.CreateForm()`이다. `FormCreator`는 `PROGRAMID`로 타입을 찾고 `Activator.CreateInstance()`로 폼을 생성한 뒤, `SmartBaseForm`에는 `UIId`, `MenuId`, `LanguageKey`, `ConnectionKey`를 설정하고 `SmartConditionBaseForm.LoadForm(parameters)`를 호출한다.

마이그레이션 폼 열기 순서는 다음과 같이 표준화한다.

1. `FormCreator.CreateForm(menuId, parameters)` 진입
2. `MenuRepository` 또는 `AuthorizationService`에서 메뉴 권한 재확인
3. 리플렉션 또는 DI로 폼 인스턴스 생성
4. 폼 생성자에서 `InitializeComponent()` 실행
5. `SmartBaseForm` 공통 초기화: EventAggregator 구독, 기본 키 처리, 메뉴 오픈 이력 생성
6. `SmartConditionBaseForm.LoadForm(parameters)` 실행
7. `InitializeConditionFromDatabase()` -> `InitializeCondition()` -> `InitializeConditionControls()`
8. `InitializeToolbar()` -> `InitializeContent()` -> `InitializeSaveConditionList()`
9. `AutoSearch` 옵션이 켜진 화면만 `OnSearchAsync()` 자동 실행

컨트롤 초기화 순서는 `컨트롤 생성` -> `언어 적용(ChangeLanguage)` -> `권한 적용(툴바 버튼 활성화)` -> `이벤트 구독`이다. 현행 `SmartBaseForm.OnLoad()`가 `ChangeLanguage()`를 호출하고, `SmartConditionBaseForm.InitializeToolbar()`가 `Toolbars` 목록으로 버튼을 생성하므로, 마이그레이션 후에는 메뉴 권한에서 내려온 오브젝트 권한을 `ToolbarPermissionService`에서 계산한 뒤 저장/삭제/엑셀/커스텀 버튼의 `Enabled`, `Visible`에 반영한다.

`EquipmentAlarmHistory` 같은 조회 화면은 `SmartConditionBaseForm`을 상속하고 `OnSearchAsync()`에서 `Conditions.GetValues()`를 읽어 필수 조건을 검증한 뒤 여러 저장 프로시저 결과를 그리드, 차트, 피벗에 바인딩한다. 이 패턴을 기준으로 조회 화면은 `OnValidateSearchCondition()`과 `OnSearchAsyncCore()`를 분리하고, 조회 조건 오류는 서버 호출 전에 차단한다.

더티 체크는 폼 공통 인터페이스로 제공한다.

```csharp
public interface IDirtyTrackableForm
{
    bool HasChanges();
}
```

`SmartConditionBaseForm.HasChanges()`는 화면 내 `SmartBandedGrid`, `SmartGrid`, 편집 컨트롤을 순회한다. 그리드는 `GetChangedRows().Rows.Count > 0`이면 변경 있음으로 판단한다. 폼 닫기, 탭 닫기, 전체 종료 시 `HasChanges()`가 `true`이면 저장 여부 확인 팝업을 표시하고, `저장 후 닫기`, `저장하지 않고 닫기`, `취소`를 제공한다.

Wait Dialog 생명주기는 현행 `pnlContent.ShowWaitArea()`와 `CloseWaitArea()` 패턴을 유지한다. `OnSearchAsync`, `OnToolbarSaveClick`, 대량 엑셀 내보내기, 설비 메시지 전송 진입 시 `Show`하고, 반드시 `try/finally`의 `finally` 블록에서 `Close`한다. 중첩 호출을 고려하여 `WaitDialogScope`는 참조 카운트를 가지며, 가장 바깥 scope가 종료될 때만 실제로 닫는다.

리소스 정리는 `OnClosed`에서 일괄 수행한다. 현행 `SmartBaseForm.OnClosed()`는 자식 컨트롤 중 `IEventAggregatorSubscriber`를 찾아 `EventAggregator.Current.UnSubscribe()`를 호출하고, 폼 자신도 구독 해제하며 메뉴 닫기 이력을 저장한다. 마이그레이션 후 추가 정리 항목은 `CancellationTokenSource.Cancel/Dispose`, `Timer.Dispose`, SignalR 그룹 구독 해제, 파일 감시자 해제, 대용량 DataTable 참조 해제이다.

### 20.3 그리드 행 상태(_STATE_) 관리 흐름

현행 `SmartBandedGrid.GetChangedRows()`는 `DataTable.GetChanges(DataRowState.Added/Modified)`와 `View.GetDeletedRows()`를 조합하고, 저장 직전에 `_STATE_` 컬럼을 추가하여 `added`, `modified`, `deleted`를 채운다. 이 방식은 DB 저장에는 충분하지만, UI에서 행 상태를 즉시 시각화하기 어렵다. 마이그레이션에서는 `_STATE_`를 그리드 표준 숨김 컬럼으로 상시 보유한다.

행 상태 값은 다음과 같다.

| 값 | 의미 | 저장 대상 |
| --- | --- | --- |
| `''` | 조회 후 초기 상태 | 제외 |
| `added` | 신규 행 | Insert |
| `modified` | 조회 행 수정 | Update |
| `deleted` | 삭제 표시 | Delete |

상태 설정 시점은 다음 기준을 따른다.

1. `SetDataSource()` 완료 후 전체 행의 `_STATE_`를 빈 문자열로 초기화하고 `AcceptChanges()`를 호출한다.
2. `AddNewRow()` 또는 신규 행 추가 버튼 처리 시 `_STATE_ = 'added'`로 설정한다.
3. `CellValueChanged` 이벤트에서 기존 행의 `_STATE_`가 빈 문자열이면 `modified`로 변경한다. 기존 값이 `added`이면 유지한다.
4. 삭제 버튼은 즉시 DataTable에서 제거하지 않고 `_STATE_ = 'deleted'`로 표시한다. 실제 제거는 저장 성공 후 수행한다.
5. 삭제 표시 행을 복원하면 원본 행은 `modified`, 신규 행은 `added` 또는 제거로 되돌린다.

시각화 규칙은 `RowStyle` 또는 조건부 서식에서 공통 처리한다. `added`는 연한 초록 배경, `modified`는 연한 노랑 배경, `deleted`는 회색 배경과 취소선을 적용한다. 삭제 표시 행은 편집 불가로 전환하고 체크박스 선택만 허용한다.

`GetChangedRows()`는 `_STATE_ != ''`인 행만 복사하여 저장 API에 전달한다. 반환 DataTable의 `TableName`은 현행과 동일하게 `list`를 기본값으로 사용하고, API 요청에서는 `{ state, values }` 배열로 변환한다. 저장 성공 후 서버가 반환한 키와 버전 정보를 원본 DataTable에 반영하고 `_STATE_`를 빈 문자열로 초기화한다.

10,000행 이상 변경 건은 일반 저장 API 대신 서버 Bulk API를 사용한다. `SmartBandedGrid.GetChangedRows()`가 10,000건 이상을 반환하면 `BulkSaveRequest`로 전환하고, UI에는 진행률과 취소 버튼을 표시한다. Bulk 처리 중 폼 닫기는 차단하고, 취소 시 서버 작업 상태를 조회하여 `Canceled`, `Completed`, `Failed` 중 하나로 마무리한다.

### 20.4 설비 상태 전이 매트릭스 동작 흐름

현행 `EquipmentStateChange` 화면은 현재 설비 목록과 변경 가능한 상태 목록을 조회한 뒤, 저장 시 `MESSAGETARGET`과 `RequestEquipmentState` 메시지를 만들어 `ServiceDispatcher`로 전달한다. 서버의 `REQUEST_EQUIPMENT_STATE`는 설비 제어 모드에 따라 직접 상태를 변경하거나 설비로 요청 메시지를 전송한다. `StateService.changeEquipmentState()`는 현재 상태와 목표 상태를 비교하고, `StdTbEquipmentStateMatrixData`의 `FROM_STATE_ID`, `TO_STATE_ID`, `SET_STATE_ID`를 조회하여 최종 상태를 보정한다.

마이그레이션 대상 매트릭스 테이블은 다음 구조로 확장한다.

| 컬럼 | 설명 |
| --- | --- |
| `PLANTID` | Site |
| `FROMSTATEID` | 현재 상태 |
| `TOSTATEID` | 요청 상태 |
| `ALLOWFLAG` | 전이 허용 여부 |
| `SETSTATEID` | 허용 시 실제 반영 상태. 현행 `SET_STATE_ID`와 호환 |
| `REQUIREREASON` | 사유 필수 여부 |
| `VALIDSTATE` | 사용 여부 |

상태 변경 전 검증은 `EquipmentStateCommunicationHandler`에서 수행한다. 요청이 수동 UI에서 왔든 설비 통신에서 왔든 동일하게 `fromState`, `toState`, `plantId`로 매트릭스를 조회하고, `ALLOWFLAG = 'Y'`인 경우만 다음 단계로 진행한다. `REQUIREREASON = 'Y'`인데 사유가 없으면 UI는 저장 전 차단하고, 설비 통신은 거부 이력과 알람을 남긴다.

수동 전이는 `EquipmentStateChange` 화면에서 현재 선택 설비의 현재 상태를 기준으로 가능한 다음 상태만 콤보 또는 그리드에 표시한다. 현행 `ept_sp_selectEquipmentStateChangeStateCode` 조회 결과에 매트릭스 조건을 추가하고, 복수 설비 선택 시 모든 설비에 공통으로 허용되는 상태만 보여준다. 상태 색상은 현행처럼 `STATECOLOR`를 사용한다.

자동 전이는 `REQUEST_EQUIPMENT_STATE` 수신 시 수행한다. 메시지 body의 요청 상태를 읽은 뒤, 캐시된 설비 현재 상태와 매트릭스를 비교한다. 허용이면 `StateService`를 통해 상태와 요약을 갱신하고, 불허이면 설비 응답에는 실패 사유를 포함한다.

상태 이력은 `EPT_TB_EQUIPMENT_STATE_HISTORY`에 저장한다. 현행 `insertStateHistory()`가 `EptTbStateData`를 기록하는 의미를 유지하며, 신규 이력에는 `EquipmentId`, `FromState`, `ToState`, `SetState`, `ChangeTime`, `UserId`, `Reason`, `SourceType(UI/EQP/SYSTEM)`, `TxnHistKey`를 포함한다.

동시성은 Optimistic Concurrency로 처리한다. 상태 변경 요청 시 클라이언트가 마지막으로 본 `CurrentStateVersion` 또는 `LastStateChangeTime`을 함께 보내고, 저장 시 현재 DB 값과 다르면 마지막 변경자 우선으로 거부한다. UI는 최신 상태를 다시 조회하고 사용자가 재시도하도록 한다.

### 20.5 FDC 실시간 수집 -> 인터락 -> 알람 처리 흐름

현행 FDC 스펙은 `ActiveParameterSpec` 화면에서 `LSL`, `LCL`, `UCL`, `USL`, `DOINTERLOCK`, `INTERLOCKRULE`, `INTERLOCKACTION`, `INTERLOCKFAULTCOUNT` 등을 관리한다. 서버 `SpecValidationService`는 수집값을 스펙과 비교하여 `OUT_OF_SPEC`, `OUT_OF_CONTROL`을 판정하고, 연속 위반 횟수가 기준을 초과하면 `REQUEST_FDC_INTERLOCK`을 호출한다.

수집 주기는 설비별로 분리한다. `TraceParameter`는 초 단위 수집 간격을, `SummaryParameter`는 분 단위 집계 주기를 가진다. 설정은 설비-파라미터 매핑 테이블에서 관리하고, 런타임에는 `FdcCollectionScheduleCache`가 설비별 수집 스케줄을 보유한다. 설정 변경 이벤트가 발생하면 해당 설비 스케줄만 재생성한다.

인터락 검출 기준은 `FDC_TB_ACTIVE_PARAMETER_SPEC`의 `USL/LSL/UCL/LCL`과 수집값 비교이다. Numeric Range 기준에서 `value < LSL` 또는 `value > USL`이면 Spec 위반, `value < LCL` 또는 `value > UCL`이면 Control 위반으로 판정한다. `INTERLOCKFAULTCOUNT`가 1보다 크면 설비, 파라미터, Lot, Recipe 조합별 연속 위반 카운트를 유지하고, 정상값 수신 시 카운트를 초기화한다.

처리 시퀀스는 다음과 같다.

1. 설비 통신 또는 수집 프로세스가 `GEN_FDC_DATA`를 수신한다.
2. `FdcSpecCheckService`가 활성 스펙을 조회하고 수집값을 판정한다.
3. 위반 감지 시 `FdcInterlockService`가 연속 위반 횟수와 인터락 룰을 확인한다.
4. 인터락 조건 충족 시 `REQUEST_FDC_INTERLOCK` 메시지를 설비로 전송한다.
5. 설비 응답을 수신하고 성공/실패 결과를 확정한다.
6. `FDC_TB_INTERLOCK_HIST`에 Plant, Equipment, SubEquipment, Parameter, Value, Lot, Recipe, Action, Result, Reason, EventTime을 저장한다.
7. SignalR Hub가 대상 설비 그룹에 인터락 이벤트를 Push한다.
8. UI는 알람 배너, 설비 상태 패널, `FDCTraceParaMonitoring` 화면에 이벤트를 반영한다.

수동 해제는 `FDCInterlockForceCancel` 화면에서 처리한다. 해제 사유는 필수이며, 해제 버튼은 인터락 해제 권한을 가진 사용자에게만 활성화한다. 해제 요청은 `FdcInterlockCancelService`를 통해 현재 인터락 상태를 재확인한 뒤 처리하고, 결과는 감사 로그와 `FDC_TB_INTERLOCK_CANCEL_HIST`에 저장한다.

실시간 모니터링 구독은 폼 생명주기에 묶는다. `FDCTraceParaMonitoring` 폼이 열리고 설비 조건이 확정되면 `SignalR Hub SubscribeEquipmentFdc(equipmentId)`를 호출한다. 폼 닫기 또는 설비 조건 변경 시 기존 그룹을 `UnsubscribeEquipmentFdc(equipmentId)`로 해제한다. 현행 일부 실시간 화면의 `Timer` 기반 갱신(`ReleaseTimer()` 패턴)은 SignalR 장애 시 폴백 조회에만 사용한다.

### 20.6 RMS 레시피 승인 워크플로우 상세

현행 RMS 영역은 `ProcessRecipe`, `SequenceRecipe`, `RecipeApproval`, `ApprovalRequest...Popup` 화면과 `REQUEST_DOWNLOAD_RECIPE` 서버 컴포넌트로 구성된다. `REQUEST_DOWNLOAD_RECIPE`는 설비와 레시피 파라미터를 조회하여 설비 메시지 body의 `RECIPEPARAMETERLIST`를 구성하고, 설비 다운로드 옵션이 켜진 경우 메시지를 전송한다.

승인 요청 생성은 `ProcessRecipe` 또는 `SequenceRecipe` 저장 시 변경 여부를 감지하는 단계에서 시작한다. 그리드와 파라미터 목록의 `GetChangedRows()` 결과가 있거나 레시피 기본 정보가 변경되면 `승인 요청` 팝업을 표시하고, 사용자가 요청 사유와 적용 범위를 입력하면 `RequestApproval` Rule을 호출한다. 승인 요청 생성과 레시피 저장은 하나의 트랜잭션으로 묶는다.

승인 경로는 `RMS_TB_APPROVAL_PATH`에서 결정한다. 조회 조건은 `PlantId`, `RecipeType`, `ProductDefId`, `EquipmentClassId`, `ChangeType`이며, 결과는 `StepNo`, `ApproverType`, `ApproverId`, `RequiredFlag`로 반환한다. 경로가 없으면 승인 요청을 차단하고 관리자 설정을 요구한다.

순차 승인 처리는 단계별 잠금 방식이다. 현재 단계 승인자에게만 승인/반려 버튼을 활성화하고, 이전 단계가 완료되지 않았으면 이후 단계는 읽기 전용으로 표시한다. 대리 승인은 권한 테이블에 등록된 경우만 허용하며, 승인/반려 시 코멘트와 전자서명 확인을 필수로 할 수 있다.

승인 대기 중 레시피는 ReadOnly 상태로 열린다. 사용자가 수정하려면 기존 승인 요청을 취소하고 새 변경본으로 재신청해야 한다. 취소 시 기존 승인 이력은 `Canceled`로 남기고, 새 승인 요청은 신규 `ApprovalRequestId`를 부여한다.

최종 승인 후 레시피 상태는 `Approved`가 되며 배포 가능 상태로 전환된다. UI는 배포 대상 설비 선택 팝업을 표시하고, 대상 설비별 현재 레시피 버전, 연결 상태, 다운로드 가능 여부를 함께 보여준다.

배포는 설비별 `REQUEST_DOWNLOAD_RECIPE` 메시지 전송으로 수행한다. 서버는 설비 응답을 수신한 뒤 `RMS_TB_RECIPE_DOWNLOAD_HIST`에 `RecipeId`, `RecipeVersion`, `EquipmentId`, `RequestTime`, `ReplyTime`, `Result`, `Message`, `UserId`를 저장한다.

배포 실패는 설비별로 분리 표시한다. 전체 대상 중 일부 성공을 허용하고, 실패 목록에는 실패 사유와 재배포 버튼을 제공한다. 재배포는 실패 설비만 기본 선택하며, 승인된 레시피 버전이 변경되었으면 기존 배포 작업을 재사용하지 않는다.

### 20.7 다중 탭(MDI) 폼 관리

현행 `MainForm`은 MDI Parent이고 `MenuRepository.OpenMenu()`는 매번 폼을 생성하여 `MdiParent`에 연결한다. 중복 화면 제어와 탭 상태 관리는 명시되어 있지 않으므로, 마이그레이션에서는 `MdiTabManager`를 도입한다.

중복 열기 정책은 `menuId + normalizedParametersHash`를 기준으로 한다. 동일 `menuId`와 동일 파라미터 조합이면 기존 탭을 활성화하고, 파라미터가 다르면 새 탭을 연다. 파라미터 해시는 정렬된 키와 값으로 생성하며, 날짜는 ISO 형식, 대소문자 무시가 필요한 키는 정규화한다.

탭 제목은 `menuName(다국어)`와 파라미터 요약을 조합한다. 예를 들어 `EquipmentAlarmHistory` 화면이 `P_EQUIPMENTID = EQ001`로 열리면 `설비 알람 이력 [EQ001]`로 표시한다. 요약 대상 키는 메뉴별 설정으로 관리하고, 없으면 메뉴명만 표시한다.

더티 탭은 제목 앞 또는 뒤에 `*`를 표시한다. `SmartBandedGrid`의 `_STATE_` 변경, 조건 화면의 편집 컨트롤 변경, 레시피 편집 화면의 파라미터 변경 이벤트가 `DirtyStateChanged`를 발생시키면 `MdiTabManager`가 탭 제목을 갱신한다.

탭 닫기는 `HasChanges()` -> 저장 확인 팝업 -> `OnClosed` 리소스 정리 순서로 처리한다. 저장을 선택하면 해당 폼의 표준 저장 명령을 실행하고, 성공한 경우에만 닫는다. 취소를 선택하면 닫기 동작을 중단한다.

전체 닫기는 앱 종료 시 더티 탭 목록을 먼저 표시한다. 사용자는 `모두 저장 후 종료`, `저장하지 않고 종료`, `종료 취소`를 선택할 수 있다. 모두 저장은 탭 순서대로 실행하며, 하나라도 실패하면 종료를 중단하고 실패 탭을 활성화한다.

메모리 관리는 최대 탭 개수 제한을 둔다. 기본값은 20개이며, 초과 시 가장 오래된 미사용 탭부터 자동 닫기를 시도한다. 더티 탭, 장시간 작업 중인 탭, 모달 자식 창이 열린 탭은 자동 닫기 대상에서 제외한다.

### 20.8 조건 저장/불러오기 UX 흐름

현행 `SmartConditionBaseForm`은 `InitializeSaveConditionList()`에서 저장 조건 메뉴를 구성하고, `barConditionSave` 클릭 시 `SaveCondition(UserInfo.Current.Id, Name, Conditions.GetValues())`를 호출한다. `ConditionSettingRepository`는 JSON 파일 기반 저장소를 사용하고, 저장 한도는 `App.config`의 `SaveConditionCount=10`을 따른다.

저장 UI는 검색 버튼 우클릭 메뉴에 둔다. 사용자가 `현재 조건 저장`을 선택하면 조건명 입력 팝업을 표시하고, 입력값과 `Conditions.GetValues()` 결과를 `ConditionSetting_{userId}_{menuId}.json`에 저장한다. 동일 조건명이 있으면 덮어쓰기 확인을 표시한다.

저장 파일 구조는 다음과 같이 표준화한다.

```json
{
  "latest": {
    "savedAt": "2026-06-09T10:00:00+09:00",
    "values": {}
  },
  "items": [
    {
      "name": "일별 설비 조회",
      "savedAt": "2026-06-09T10:00:00+09:00",
      "values": {}
    }
  ]
}
```

불러오기 UI는 검색 버튼 우클릭 메뉴의 `저장된 조건` 서브메뉴에 조건명 목록을 표시한다. 조건을 선택하면 해당 값을 `Conditions.SetValue()`로 반영하고, 기간 조건 등 특수 컨트롤은 컨트롤 타입별 어댑터가 변환한다. 적용 후 자동 조회 여부는 화면 옵션으로 결정한다.

삭제는 조건 목록에서 `Delete` 키 또는 우클릭 `삭제` 메뉴로 수행한다. 삭제 전 확인 팝업을 표시하고, 삭제 후 메뉴 목록을 즉시 갱신한다. `latest` 항목은 수동 삭제 대상에서 제외하고 `최근 조건 초기화` 메뉴로만 비운다.

자동 저장은 조회 성공 시 마지막 조회 조건을 `latest` 키로 저장한다. 폼 재오픈 시 `latest`가 있으면 조건을 자동 복원하되, 메뉴 링크 파라미터로 넘어온 조건이 있으면 링크 파라미터를 우선한다. `FDCTraceParaMonitoring`처럼 링크 메뉴 파라미터 `EQUIPMENT`를 받는 화면은 링크 파라미터 적용 후 나머지 조건만 `latest`에서 보완한다.

저장 한도는 메뉴당 최대 10개이다. 초과 시 `savedAt`이 가장 오래된 사용자 저장 조건을 자동 삭제한다. `latest`는 한도에 포함하지 않는다.

### 20.9 실시간 모니터링 업데이트 주기 및 UI 갱신 전략

현행 실시간 화면 일부는 `System.Windows.Forms.Timer`를 사용하고, 종료 시 `ReleaseTimer()`에서 Stop, Tick 해제, Dispose를 수행한다. 마이그레이션에서는 설비 상태, 알람, FDC 데이터를 SignalR 기반 이벤트로 전환하되, 장애 시 주기 조회를 폴백으로 둔다.

업데이트 방식은 데이터 성격별로 분리한다. 설비 상태 변경 이벤트는 즉시 반영하고, FDC 데이터는 수집 주기마다 배치로 반영한다. 알람 발생/해제는 즉시 Push하되, 대량 알람 동기화는 1초 단위로 병합한다.

Throttle 정책은 설비 단위로 적용한다. 동일 설비 상태가 1초 내 여러 번 들어오면 마지막 값만 UI에 반영하고, 중간 값은 상태 이력에는 저장하되 화면 렌더링에서는 생략한다. FDC 차트는 수집값을 버리지 않고 버퍼에 쌓은 뒤 렌더링만 배치 처리한다.

웹(Blazor Server) 구현의 표시 등가성 한계를 명시한다. 웹 화면의 배치 갱신은 클라이언트 버퍼 누적이 아니라 '최신 N건 스냅숏 재조회'로 구현하므로, 1초 윈도 안에 N건을 초과하는 고속 수집이 발생하면 중간 샘플은 화면에 렌더링되지 않는다. 데이터 무손실 원칙은 서버 DB 저장으로 보장되며(전량 저장), 화면은 어디까지나 최신 구간의 표시 창이다. 중간 샘플 분석이 필요한 경우 이력 조회 화면을 사용한다.

UI 스레드 전환은 모든 실시간 수신 핸들러의 공통 규칙이다. SignalR 수신은 백그라운드 스레드에서 발생할 수 있으므로, WinForms 화면은 `Control.BeginInvoke()`를 통해 UI 스레드에서 그리드와 차트를 갱신한다. 폼이 이미 닫혔거나 `IsDisposed`이면 수신 이벤트를 무시한다.

탭 비활성화 최적화는 렌더링만 멈추는 방식으로 처리한다. 다른 탭으로 전환되어도 SignalR 구독은 유지하고 최신 데이터는 화면 ViewModel에 저장한다. 탭이 다시 활성화되면 누적된 최신 상태를 한 번에 반영하고, 차트는 마지막 표시 시점 이후 데이터만 추가한다.

웹 MDI에서는 이 조항을 다음과 같이 적응한다. 웹의 탭 전환은 페이지 컴포넌트 dispose이므로 WinForms처럼 비활성 탭의 SignalR 구독을 유지할 수 없다 — 구독은 페이지 수명과 함께 해제된다. 대신 조회 조건·실시간 감시 여부 등 화면 상태를 서킷 수명(Scoped) 상태 서비스(예: `FdcMonitorState`)에 보존하고, 탭 재활성화 시 전체 재조회 1회로 '누적분 일괄 반영'을 대체하며 감시를 자동 재개한다. 차트 증분 추가도 전체 재조회로 대체한다. 탭에서 벗어나 있는 동안의 수집값·이벤트는 서버에 저장되므로 데이터 유실은 없고, 수신 시점 화면 표시(예: 인터록 화면 로그)만 기록되지 않는다 — 정식 이력은 해당 이력 화면에서 조회한다.

연결 끊김 재시도는 `HubConnection.Reconnecting`, `Reconnected`, `Closed` 이벤트를 사용한다. 재연결 중에는 화면 상단에 진행 상태를 표시하고, 성공 시 기존 구독 그룹을 모두 재구독한다. 재연결 실패가 일정 횟수를 초과하면 폴백 조회 모드로 전환하고 사용자에게 수동 재연결 버튼을 제공한다.

### 20.10 비밀번호 분실 처리 흐름

현행 사용자 관리 영역에는 초기화/재생성 성격의 서버 Rule(`InitUserPassword`, `RegenPassword`, `ChangeUserPassword`)과 메일 발송 서비스가 존재한다. 비밀번호 분실 플로우는 로그인 전 사용자 셀프서비스이므로, 관리자 초기화와 분리된 `forgot-password` API로 구현한다.

신청 흐름은 `LoginForm`의 `[비밀번호 분실]` 클릭에서 시작한다. 사용자는 아이디와 이메일을 입력하고, 클라이언트는 `POST /api/v1/auth/forgot-password`를 호출한다. 보안을 위해 아이디 또는 이메일이 틀려도 화면에는 동일한 일반 메시지를 반환하고, 서버 로그에만 상세 사유를 남긴다.

토큰 생성은 GUID 또는 암호학적 난수 기반 일회용 토큰으로 처리한다. 서버는 토큰 해시를 `SYS_TB_USER.RESET_TOKEN`, 만료 시각을 `RESET_TOKEN_EXPIRE`에 저장하고 유효 시간은 24시간으로 제한한다. 원문 토큰은 저장하지 않는다.

구현에서는 재설정 링크(RESET_TOKEN) 방식 대신 본 절이 함께 인정하는 **임시 비밀번호 방식을 채택**한다. 서버가 암호학적 난수(`RandomNumberGenerator`) 기반 임시 비밀번호 12자를 발급해 즉시 해시로 저장하고 원문은 메일로만 전달하므로, `RESET_TOKEN`/`RESET_TOKEN_EXPIRE` 컬럼과 별도 만료 관리가 필요 없다. 재설정 링크 방식은 외부 접근 가능한 웹 URL 전제가 필요해 사내망 MES 환경에 부적합하다는 점도 고려했다. 기존 `POST /api/v1/auth/reset-password`는 서버 내부에서 `forgot-password`와 동일하게 위임 처리해 같은 202를 반환한다 — 301 리다이렉트는 클라이언트가 따라갈 때 POST가 GET으로 바뀌고 본문이 소실되어 실제로 동작하지 않으므로 채택하지 않는다.

이메일은 기존 메일 템플릿 구조를 따른다. 템플릿 경로는 `Config/Mail/InitPassword({culture}).xml`이며(예: `InitPassword(ko-KR).xml`), `${USERNAME}`, `${PASSWORD}` 플레이스홀더를 치환한다. 사용자 언어의 템플릿이 없으면 ko-KR로 폴백하고, 그것도 없으면 평문 본문으로 폴백해 발송 자체는 보장한다. 메일 발송 실패는 서버 로그에만 남기고 API 응답(202)은 동일하게 유지한다.

임시 비밀번호 로그인 시 사용자의 상태를 `Forgot`으로 표시한다. 인증은 허용하되 첫 화면 진입 전에 `ChangePasswordOnLogin` 폼을 강제 표시하고, 비밀번호 변경이 완료되기 전에는 메뉴 로딩과 업무 API 호출을 차단한다.

상태와 차단은 다음과 같이 구현한다. 비밀번호 상태는 `SYS_USER.PASSWORD_STATE` 컬럼(Normal/Create/Forgot/Expired, §19.2.1)으로 영속화하고, 로그인 시 상태가 Normal이 아니면 액세스 토큰에 `pwdChange` 클레임을 실어 발급한다. 서버 측 차단은 `PasswordChangeRequiredMiddleware`가 클레임만으로 판정해(요청당 DB 조회 없음) `/api/v1/auth/*`, `/health` 외 모든 경로에 403 `PASSWORD_CHANGE_REQUIRED`를 반환한다. 토큰 갱신(`refresh`) 시에는 구 액세스 토큰의 클레임을 승계하지 않고 DB 상태(`PASSWORD_STATE`, 활성/삭제 여부)를 재평가해 새 토큰을 발급한다 — 클레임 승계 방식은 Authorization 헤더 없이 갱신을 호출하면 클레임이 소실되어 차단을 우회할 수 있고, 비활성/삭제 사용자의 갱신도 끊어야 하기 때문이다. 비밀번호 변경 요청은 §19.2.2 복잡도 정책을 서버가 최종 검증하며(위반 시 400 `PASSWORD_POLICY_VIOLATION`, 평문이 존재하는 API 계층의 `PasswordPolicy`에서 수행), 변경 성공 시에는 §19.2.4-7에 따라 기존 리프레시 토큰을 전부 폐기해 다른 기기 세션을 만료시킨 뒤 클레임 없는 새 액세스/리프레시 토큰을 응답으로 재발급해 클라이언트가 즉시 교체한다(이전 액세스 토큰은 만료까지 차단 상태로 남는다). 웹 클라이언트는 로그인 응답의 `requirePasswordChange`로 변경 화면에 강제 진입시키고, 레이아웃 진입 시 `pwdChange` 클레임을 재확인해 우회 진입을 막는다.

실패 보안은 계정과 IP를 모두 고려한다. 5회 연속 실패 시 계정을 30분 잠그고 `SYS_TB_LOGIN_FAILURE_HIST`에 사용자, IP, UserAgent, 실패 사유, 발생 시각을 기록한다. 잠금 해제는 시간 만료 또는 관리자 해제로만 처리한다.

잠금 구현 세부는 다음과 같다. 테이블명은 구현 DB 네이밍 규약(TB 접두 없음)에 따라 `SYS_LOGIN_FAILURE_HIST`로 하며, 존재하지 않는 사용자 ID의 시도도 기록해야 하므로 `SYS_USER`와 FK를 두지 않는다. 실패 사유는 UserNotFound/WrongPassword/InactiveUser/AccountLocked 코드로 구분한다. 연속 실패 카운터(`SYS_USER.FAIL_COUNT`)와 잠금 만료 시각(`LOCKED_UNTIL`)은 사용자 행에 영속화하고, 성공 로그인이 카운터를 리셋하며, 잠금이 시간 만료된 뒤의 실패는 새 시도 구간으로 1회부터 다시 센다. 실패 카운터 증가는 read-modify-write가 아니라 단일 원자 UPDATE(`FAIL_COUNT` 증가·임계 도달 시 `LOCKED_UNTIL` 설정·`OUTPUT`으로 잠금 시각 반환)로 처리한다 — 동시 로그인 실패 시 카운트 유실을 막고, 조회 시점과 저장 시점 사이에 변경된 다른 컬럼(예: `PASSWORD_HASH`)을 전체 행 UPDATE가 덮어쓰는 사고를 차단한다. 로그인 401 응답은 `INVALID_CREDENTIALS`(계정 존재 여부를 드러내지 않는 동일 메시지)와 `ACCOUNT_LOCKED`(남은 시간 안내 메시지 노출)를 code로 구분해 화면이 잠금 안내를 표시할 수 있게 한다. 관리자 해제는 `PUT /api/v1/sys/users/{userId}/unlock`(ADMIN 전용)으로 노출하고, 사용자 관리 화면이 잠금 상태(`lockedUntil`) 행에 잠금 해제 버튼을 표시한다. 사용자 목록/조회 API는 도메인 엔터티를 그대로 직렬화하지 않고 DTO로 매핑해 `PASSWORD_HASH`가 응답에 노출되지 않게 한다.

### 20.11 배포 파일 업로드/클라이언트 자동 업데이트 흐름

현행 소스에는 배포 파일 업로드와 클라이언트 자동 업데이트가 업무 화면 수준으로 명확히 분리되어 있지 않다. 마이그레이션에서는 `DeployFileUpload` 화면과 `/api/v1/deploy` API를 신규 표준 기능으로 정의한다.

업로드는 `DeployFileUpload` 화면에서 파일 선택, 버전, 설명, 강제 업데이트 여부를 입력한 뒤 `POST /api/v1/deploy/upload`로 전송한다. 서버는 파일 해시를 계산하고, 저장소에 파일을 보관한 뒤 `SYS_TB_DEPLOY_FILE`에 `Version`, `FileName`, `Hash`, `Size`, `Description`, `ForceUpdate`, `UploadedBy`, `UploadedTime`을 저장한다.

버전 체크는 클라이언트 시작 시 수행한다. 현재 실행 파일 버전과 `GET /api/v1/deploy/latest` 응답의 최신 버전을 비교하고, 최신 버전이 더 높으면 업데이트 정책을 확인한다. 응답에는 `version`, `downloadUrl`, `hash`, `forceUpdate`, `releaseNote`를 포함한다.

강제 업데이트는 서버 응답의 `forceUpdate: true`로 제어한다. 업무 중 강제 업데이트가 감지되면 사용자에게 즉시 재시작 안내 팝업을 표시하고, 저장되지 않은 탭이 있으면 저장 또는 취소를 먼저 처리한다. 강제 업데이트를 거부한 사용자는 업무 API 호출을 제한할 수 있다.

선택적 업데이트는 백그라운드 다운로드를 사용한다. 다운로드 완료 후 해시를 검증하고, 설치 파일은 다음 종료 시 적용한다. 다운로드 중 네트워크가 끊기면 이어받기를 시도하고, 실패하면 기존 버전으로 계속 실행한다.

롤백은 이전 버전 파일을 보관하는 방식으로 처리한다. 설치 실패, 해시 불일치, 실행 실패가 감지되면 자동으로 이전 버전을 복원하고 실패 이력을 서버에 전송한다. 서버 관리 화면은 실패율이 높은 버전을 비활성화할 수 있어야 한다.

구현은 본 절을 다음과 같이 적응한다. 테이블명은 구현 DB 네이밍 규약(TB 접두 없음)에 따라 `SYS_DEPLOY_FILE`(V012)로 하고, 업로드 엔드포인트는 컬렉션 리소스 규약에 따라 `POST /api/v1/deploy/files`(multipart/form-data, ADMIN 전용)로 정의한다. 버전 문자열은 `System.Version`으로 파싱 가능한 형식만 허용한다 — 최신 버전 선정이 문자열 정렬이 아니라 숫자 세그먼트 비교(`1.9.0` < `1.10.0`)로 동작해야 하므로 `v` 접두사나 `-beta` 같은 시맨틱 접미사는 업로드 시점에 차단하고(NVARCHAR(20) 한도 포함, VERSION UNIQUE 제약), 해시는 SHA-256으로 서버가 저장 시점에 계산한다. 파일명은 다운로드 응답 헤더에 그대로 쓰이므로 경로 성분이나 제어 문자가 있으면 거부하고, 전송 중단으로 부분 기록된 바이너리와 메타 기록에 실패한 바이너리는 즉시 삭제해 고아 파일을 남기지 않는다. 바이너리는 DB가 아니라 API 서버 디스크(`IDeployFileStorage`)에 GUID 파일명으로 보관하고 메타데이터만 테이블에 기록한다.

`GET /api/v1/deploy/latest`와 파일 다운로드는 `[AllowAnonymous]`로 노출한다 — 클라이언트 업데이트 체크는 로그인 전 기동 시점에 일어나고 사내망 전제이므로 인증을 요구하지 않는다. 다운로드는 `enableRangeProcessing`으로 HTTP Range 이어받기를 지원해 '다운로드 중 네트워크가 끊기면 이어받기' 조항을 서버 측에서 뒷받침한다. 문제 버전 회수는 `PUT /api/v1/deploy/files/{fileId}/deactivate`(ADMIN)로 구현하고, 비활성 파일은 latest 선정과 다운로드에서 모두 제외된다 — 실패 이력 자동 수집과 자동 롤백은 클라이언트 에이전트 영역이므로 이번 서버 구현 범위에서 제외하고 관리자 수동 회수로 대응한다.

관리 화면은 `DeployFiles.razor`(`/sys/deploy`, ADMIN)로 구현한다. 파일 상한 500MB는 서버 명시 검사와 화면 `OpenReadStream` 양쪽에 동일하게 걸되, 요청 본문 한도(`[RequestSizeLimit]`)는 multipart 오버헤드 여유분을 더해 경계 크기 파일이 413으로 거부되지 않게 한다. Web→API HttpClient는 전역 타임아웃을 업로드 상한(10분)으로 두고 일반 요청은 위임 핸들러가 기본 100초로 제한해, 대용량 업로드만 장시간을 허용한다. multipart 업로드 본문은 스트림이라 되감을 수 없으므로 401 재시도 없이 사전 토큰 갱신 후 1회 전송한다.

### 20.12 즐겨찾기/최근 메뉴 동작 흐름

현행 `SmartBaseForm.AddFavorite()`는 `IFavoriteRepository`를 통해 즐겨찾기를 저장하고, `FavoriteSettingRepository.AddFavorite()`는 `SaveFavoriteMenu` Rule에 `_STATE_ = 'added'` 데이터를 전달한다. `FavoriteSetting` 화면은 그리드 드래그 앤 드롭으로 `DISPLAYSEQUENCE`를 변경하고 저장한다. 최근 메뉴는 `RecentMenuSettingRepository`가 `%AppData%` 하위 JSON 파일로 저장하며, `App.config`의 `RecentMenuCount=10`을 사용한다.

즐겨찾기 추가는 메뉴바 우클릭 또는 폼 내부 `즐겨찾기 추가` 버튼에서 수행한다. 현재 `MenuId`, `UIId`, 로그인 사용자 ID를 기준으로 `AddFavorite()`를 호출하고, 서버는 `SaveFavoriteMenu` Rule로 `SYS_TB_FAVORITE_MENUS` 또는 호환 테이블에 저장한다. 이미 등록된 메뉴이면 중복 추가하지 않고 기존 항목을 활성화한다.

즐겨찾기 순서 변경은 즐겨찾기 메뉴 편집 팝업에서 드래그 앤 드롭으로 처리한다. 화면은 변경된 행의 `DISPLAYSEQUENCE`와 `_STATE_ = 'modified'`를 설정하고, 저장 시 `SaveFavoriteMenu` Rule로 전달한다. 저장 성공 후 메뉴바의 즐겨찾기 영역을 즉시 재렌더링한다.

최근 메뉴는 폼 열기 성공 시 자동 추가한다. 동일 메뉴와 동일 파라미터 조합이 이미 있으면 기존 항목을 맨 앞으로 이동하고, 없으면 신규 항목을 맨 앞에 추가한다. 최대 10개를 유지하며 초과 항목은 가장 오래된 항목부터 제거한다.

권한 제거 시 즐겨찾기와 최근 메뉴에서는 해당 항목을 숨긴다. 데이터는 삭제하지 않고 `AuthorizedMenus` 결과와 교차 확인하여 렌더링에서 제외한다. 권한이 복원되면 기존 즐겨찾기 순서와 최근 메뉴 순서를 유지하여 다시 표시한다.

구현은 본 절을 다음과 같이 적응한다. 저장소는 `SYS_FAVORITE_MENU`/`SYS_RECENT_MENU` 테이블(V013, USER_ID+MENU_ID 복합 PK)로 하고, `SaveFavoriteMenu` Rule 대신 `GET/POST/DELETE /api/v1/sys/favorites`, `PUT /api/v1/sys/favorites/order`, `GET/POST /api/v1/sys/recent-menus` REST API로 정의한다. 최근 메뉴는 현행의 클라이언트 `%AppData%` JSON이 아니라 서버 DB에 저장한다 — Blazor Server에는 클라이언트 로컬 파일이 없고, 서버 저장이면 어느 브라우저/PC에서 접속해도 동일한 최근 목록이 유지된다. 한도는 즐겨찾기 50개/최근 10개로 하며, 최근 메뉴는 동일 메뉴 재방문 시 `LAST_USED_AT`만 갱신하는 Upsert로 '기존 항목을 맨 앞으로 이동'을 구현하고 초과분은 오래된 것부터 삭제한다. 현행의 '동일 메뉴+동일 파라미터 조합' 단위 기록은 메뉴 단위 기록으로 단순화한다 — 웹 MDI 탭이 메뉴 단위로 열리므로 파라미터 차이는 구분하지 않는다.

대상은 Screen 타입 메뉴로 한정하고(Folder는 즐겨찾기·최근 모두 불가), 메뉴 ID 비교는 대소문자 무시로 하되 저장 시에는 메뉴 마스터의 정식 `MenuId`로 정규화해 변형 ID 중복 행을 차단한다. 즐겨찾기 중복 추가는 오류가 아니라 멱등 성공으로 처리하고(기존 항목·순서 유지), 권한 밖 메뉴의 즐겨찾기 추가는 400을 반환하되 최근 메뉴 기록은 조용히 무시하고 204를 반환한다 — 최근 기록은 화면 탐색의 부수 효과라 본 동작을 오류로 만들지 않는다. 권한 제거 시 행은 유지하고 조회 시 권한 메뉴와 교차해 숨기는 원문 조항은 그대로 구현했으며, 순서 변경은 변경된 행만 UPDATE하고 목록에 없는 기존 항목은 뒤로 보낸다.

웹 UI는 드래그 앤 드롭 편집 팝업 대신 NavMenu 내 인라인 편집 모드로 적응한다. 즐겨찾기 섹션 헤더의 편집 토글을 켜면 각 항목에 ▲/▼(순서)·✕(삭제) 버튼이 나타나고, 일반 메뉴 항목에 마우스를 올리면 ☆/★ 토글로 추가/삭제한다. 최근 메뉴 기록은 MDI 탭이 열리는 시점(`MdiTabBar`)에 fire-and-forget으로 호출해 화면 전환을 지연시키지 않으며, 기록 실패는 화면 동작에 영향을 주지 않는다.

---

*본 문서는 SmartUX3.5_20260526 소스 분석을 기반으로 작성된 C# 마이그레이션 상세설계서입니다.*  
*각 Phase 착수 시 세부 구현 명세를 추가 작성하여 보완합니다.*

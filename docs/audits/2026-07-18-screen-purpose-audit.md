# 2026-07-18 코드 시드 화면 목적 감사

## 결론

canonical 코드 시드 270개를 실제 active surface 기준으로 다시 감사했다. legacy alias 13개는 같은 `ScreenDefinition`을 공유하므로 중복 계수하지 않았다.

변경 전 `Auto`는 160개였고 구조 분포는 다음과 같았다.

| 구조 분류 | 수 | 판정 근거 |
|---|---:|---|
| 읽기 전용 완성 | 128 | primary query가 있고 편집 입력, 저장, 삭제, 변경 명령이 없음 |
| 편집·저장 완성 | 8 | 편집 입력과 실제 save path가 함께 있음 |
| 구현 공백 | 24 | 22개는 읽기 전용 surface지만 과거 write intent로 보류, 2개는 완성된 read/write 계약이 없음 |
| 실행 완성 | 0 | `Auto` 중 도달 가능한 mutation-only 실행 화면 없음 |

검토 결과 150개를 `Inquiry`, 8개를 `Manage`로 명시했다. 현재 enum 중 하나를 붙이면 실제 기능을 과장하는 2개만 `Auto`로 유지하고 코드에 사유를 고정했다.

최종 canonical 분포는 `Auto 2 / Inquiry 177 / Report 53 / Manage 29 / Register 8 / Execute 1`이다.

## 판정 방법

화면 이름이나 메뉴 접미사는 판정 근거로 사용하지 않았다. `ScreenDefinitionCapabilityValidator`와 command descriptor를 사용해 현재 렌더되는 surface만 확인했다.

- flat 화면: `Columns + QueryId`, 편집 가능한 `Fields`, `SaveQueryId`, `DeleteQueryId`, bulk command
- layout 화면: 실제 `GridWidget`, `FormWidget`, `ButtonWidget`, KPI·badge·trend query와 선택 가능한 grid
- command: catalog descriptor의 mutating/non-mutating 효과
- 보조 조회: count와 option query는 primary read로 승격하지 않음
- 목적 구분: 행 단위 원본 목록은 `Inquiry`, 집계·현황·분석은 `Report`, 생성·변경 입력과 save path가 함께 있으면 `Manage` 또는 `Register`

이번 `Auto` 읽기 전용 대상은 모두 행 단위 조회 surface였다. 따라서 쓰기 기능이 구현되기 전 `Manage`나 `Register`로 표시하지 않고 `Inquiry`로 고정했다.

## 명시적 Manage 전환 8개

다음 화면은 편집 입력과 save path가 모두 있고, 화면 성격이 단건 등록 전용이 아니라 목록과 설정·CRUD를 함께 제공하므로 `Manage`로 판정했다.

- `EES_EPT_INTERESTED_INDEX_MANAGEMENT`
- `EES_FDC_REAL_TIME_USER_MONITORING`
- `EES_FDC_VIRTUAL_EVENT_MANAGEMENT`
- `FACTORY_MDM_PLANT`
- `FACTORY_PRC_PURCHASE_ORDER`
- `SYSTEM_2_BATCH_PROC_MANAGEMENT`
- `SYSTEM_2_MENU_AUTH_MANAGEMENT`
- `SYS_MENU_MGMT`

## 과거 implementation gap 22개

다음 화면은 업무 이름에 등록·관리 의미가 있어 과거 write intent 후보로 보류했지만, 현재 active surface는 primary query와 읽기 전용 grid만 제공한다. 현재 제품 계약을 정확히 표시하기 위해 `Inquiry`로 판정했다. 향후 입력과 save path를 구현하면 같은 capability gate를 통과시키며 `Register` 또는 `Manage`로 변경해야 한다.

- `QMS_4M_CHANGE_HISTORY`
- `QMS_CLM_CLAIM_REGISTRATION`
- `QMS_CLM_CLAIM_RESULT`
- `QMS_GAUGE_CALIBRATION_PLAN`
- `QMS_GAUGE_CALIBRATION_RESULT`
- `QMS_GAUGE_MEASURE_EQUIPMENT_MANAGEMENT`
- `QMS_GAUGE_REPAIR_RESULT`
- `QMS_GAUGE_RNR_PLAN`
- `QMS_GAUGE_RNR_RESULT`
- `QMS_INSP_LONGTERM_PRODUCT_INSP_RESULT`
- `QMS_LONGTERM_INSP_RESULT`
- `QMS_QCA_NCR_ISSUE`
- `QMS_QCA_RELEASE_HOLD_REG`
- `QMS_SPM_ADMIN_ACTION_RESULT_REGISTRATION`
- `QMS_SPM_EVL_DEF`
- `QMS_SPM_EVL_ITEM`
- `QMS_SPM_EVL_PARA`
- `QMS_SPM_EVL_RESULT`
- `QMS_STD_INSP_DEF`
- `QMS_STD_INSP_INCOMING_METHOD`
- `QMS_STD_INSP_ITEM`
- `QMS_STD_INSP_SPEC`

## Auto 유지 2개

| UI ID | 유지 사유 | 명시 목적을 붙이지 않은 이유 |
|---|---|---|
| `DEMO_PARAM` | 입력 메타데이터 렌더링 예제이며 조회·저장·명령 경로를 의도적으로 제공하지 않음 | `Register`·`Manage`는 거짓 저장 affordance가 되고 `Inquiry`·`Report`는 primary read 계약이 없음 |
| `SYSTEM2_CONTENTMAPPINGSERVICE_MANAGEMENT` | 명명 쿼리 아키텍처로 대체된 기능의 정적 안내 화면 | 데이터 조회·변경 surface가 없어 다섯 명시 목적 중 어느 것도 충족하지 않음 |

## 재발 방지

`SeedScreenPurposeDecisions`가 158개 전환 결정과 2개 Auto 유지 사유를 단일 catalog로 관리한다. provider는 canonical 시드 등록이 끝나는 시점에 다음을 fail-fast한다.

1. 결정 대상 UI ID가 사라지거나 이미 다른 목적이면 실패
2. 결정한 목적이 capability validator 오류를 만들면 실패
3. 유지 사유가 없거나 빈 `Auto`가 하나라도 생기면 실패
4. legacy alias는 결정 적용 후 같은 canonical 정의를 공유

집중 테스트는 결정 수, 최종 목적 분포, 명시 목적 capability 오류 0건, 남은 Auto와 유지 사유의 정확한 일치를 고정한다.

## 검증 기록

- `dotnet test test/NexaOne.UnitTests/NexaOne.UnitTests.csproj --filter 'FullyQualifiedName~ScreenPurposeMigrationAuditTests|FullyQualifiedName~ScreenDefinitionProviderTests|FullyQualifiedName~ScreenDefinitionCapabilityValidatorTests' --no-restore`: **166/166 통과**
- `dotnet test test/NexaOne.UnitTests/NexaOne.UnitTests.csproj --no-restore`: **1,461/1,461 통과**
- 새 결정 catalog와 목적 감사 테스트에 대한 `dotnet format --verify-no-changes --no-restore --include ...`: **통과**
- 화면 시드·binding·영속 Server 집중 검증은 격리 artifacts 경로에서 **42건 통과**했다. 함께 선택한 전체 호스트 boot smoke 1건은 이 변경과 무관한 `nexaone.messaging` driver 미등록으로 실패했다.

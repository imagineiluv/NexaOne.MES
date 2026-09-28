# Unified LOT / Inventory Framework Migration

## 목적
NexaOne.MES에서 Material LOT과 Production LOT이 서로 다른 식별/조회 경계를 갖고 재고 집계가 이원화될 수 있는 문제를 해결한다. 범용 제조 변형은 NexaFramework.Service에서 수용하고, NexaOne.MES는 IVT/POM/QMS의 도메인 책임과 산업·고객 특화 흐름을 유지한다.

## 핵심 원칙
1. LOT의 정체성은 하나다. 입고 생성인지 생산 생성인지는 Origin으로 기록한다.
2. 품목의 추적 정책은 NONE / LOT / SERIAL / LOT_SERIAL을 지원한다.
3. Barcode/Handling Unit은 LOT과 독립한다. 한 LOT에 여러 물류단위가 존재할 수 있고 비LOT 품목에도 물류단위를 부여할 수 있다.
4. 현재고의 Single Source of Truth는 통합 Inventory다.
5. 생산 LOT은 생산 후 재고, 출하, 다음 WO 투입 등 역할이 바뀌어도 동일 LotId를 유지한다.
6. Framework는 범용 계약·정책을 제공하고 MES는 저장/트랜잭션/권한/산업 특화 업무를 소유한다.

## Framework 선행 작업
- TrackingPolicy 추가: None, Lot, Serial, LotSerial
- LotIdentity 및 LotOrigin 추가
- HandlingUnit/Barcode 식별 계약 추가
- InventoryIdentity 추가: Variant + Warehouse + optional Lot + optional HandlingUnit
- 기존 StockPosting/StockBalance API는 즉시 제거하지 않고 호환 유지
- 신규 계약을 이용하는 V2/확장 경로를 먼저 제공한 뒤 소비자를 단계적으로 이동

## NexaOne.MES 후속 작업
### IVT
- MaterialLot를 유일한 LOT 정체성으로 취급하지 않도록 변경
- 통합 LotId를 기준으로 Receive/Move/Issue/Consume/Ship/Adjust 수불을 연결
- 비LOT 품목은 LotId 없이 수량 기반 수불 허용
- HandlingUnit을 LOT과 독립적으로 연결

### POM
- ProductionLot의 별도 정체성을 제거하고 공통 LotId를 참조
- POM은 WorkOrder/Route/Operation 등 생산 실행 상태를 소유
- 생산완료 시 통합 Inventory에 동일 LotId로 재고 반영

### QMS
- ProductionLotDirectory -> MaterialLotDirectory 순차 조회 제거
- 공통 LotDirectory/Identity 조회로 전환
- 검사 대상의 출생 경로와 무관하게 동일 LotId 사용

## 반제품 시나리오 승인 기준
1. WO에서 반제품 LOT A를 생산한다.
2. LOT A는 재고에 한 번만 생성된다.
3. LOT A 일부를 고객에게 출하할 수 있다.
4. LOT A 일부를 다음 WO의 자재로 투입할 수 있다.
5. 두 흐름 모두 동일 LotId genealogy를 유지한다.
6. Material/Production 재고 중복 집계가 발생하지 않는다.

## 마이그레이션 순서
1. NexaFramework.Service에 additive 공통 계약 추가
2. Framework 계약 테스트 추가
3. NexaOne.MES에 adapter 추가
4. IVT 수불/재고를 통합 identity로 전환
5. POM ProductionLot를 공통 LotId 참조로 전환
6. QMS 이중 LOT 조회 제거
7. 반제품 출하 + 재투입 통합 테스트
8. 레거시 MaterialLot/ProductionLot 계약 사용처 제거 후 별도 단계에서 obsolete 처리

## 비목표
- 고객별 LOT 번호 규칙
- 자동차 GP12 등 산업 특화 품질 흐름
- 특정 라벨 포맷/프린터 규칙
- Framework에서 DB schema나 MES transaction boundary를 직접 소유하는 것

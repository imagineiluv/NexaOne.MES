# SmartUX 업무화면 마이그레이션 — 추출 자동화 결과 및 계획

> 2026-06-23. 소스: SmartUX 3.5 메타 DB `PRODUCT_SMARTFACTORY_3_5` (MSSQL 100.114.16.91, 자사 한정).
> 새 시스템: NexaOne 통합 호스트 — 메타 런타임(`/meta` = ScreenDefinition + 명명쿼리) + SmartUX 테마 + 데이터 메뉴(`SYS_MENU`).

## 추출 결과 (자동화 1차)
- **메뉴(SYS_TB_MENU)**: Valid **420행 = 폴더 112 + 화면(SCREEN) 308**, 대분류 ~28개(영업 SLS·PPM 생산·WPM·품질·창고 IVT·출하 DLV·구매 PRC·EMS·EPT·FDC·QMS·MDM·COM·시스템관리 + 식품 라인 김치/피자/HMR/냉동밥/포장 등). 전체 트리를 `smartux-menu-inventory.json`으로 추출(menuId/menuName/parentMenuId/displaySequence/menuType/uiId/programId).
- 메뉴 컬럼은 새 `SYS_MENU`와 거의 1:1 → **메뉴는 자동 이식 가능**.

## 핵심 제약 (실측)
- **화면 설계 본문은 DB에서 깔끔히 추출 불가.** `UX_PROJECTFILE`(1125행)는 파일 메타(FILENAME/FILEPATH/FILEEXT/PROJECTID)만 보유 — 실제 화면 레이아웃은 앱 서버 **파일시스템**에 저장(SmartUX 화면설계기 산출물). 따라서 DB만으로 화면 콘텐츠를 ScreenDefinition으로 자동변환할 수 없다.
- 화면 콘텐츠 마이그는 **화면 단위 작업**이며, 그나마 **새 시스템에 백엔드(테이블·명명쿼리)가 있는 화면**만 실제 동작한다(빈 껍데기는 무의미).
- 엔티티/컬럼 메타(`SYS_TB_OBJECT_ATTRIBUTE`)는 그리드 컬럼·폼 필드 후보 자동 도출에 활용 가능(백엔드 존재 화면 한정).

## 권장 진행
1. **메뉴 트리 이식(자동)**: 420행 → 새 `SYS_MENU` 시드 → 사이드바가 실제 SmartUX 내비를 반영. 화면은 마이그될수록 UI_ID 매칭으로 점등(미구현 화면은 안내 표시). 식품 라인 등 비대상 모듈은 필터링 옵션.
2. **화면 콘텐츠 — 모듈 배치**: 백엔드 있는 모듈부터(MDM/기준정보·QMS/품질·POM/생산·SHP/출하·EST·FDC) 화면별 ScreenDefinition 작성. 그리드 컬럼은 SYS_TB_OBJECT_ATTRIBUTE + 기존 명명쿼리로 반자동.
3. **백엔드 없는 화면**: 해당 모듈 백엔드 마이그 선결(별도 작업).

## 산출물
- `smartux-menu-inventory.json` — SmartUX Valid 메뉴 420행 전수(추출 자동화 산출, 재실행 가능).

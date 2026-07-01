using System.Collections.Concurrent;

namespace NexaOne.Web.Services.Meta;

/// <summary>인메모리 화면 정의 제공자(Phase 3). 시작 시 데모 화면을 시드한다. 싱글톤으로 등록되며
/// 읽기는 동시 안전(ConcurrentDictionary). 향후 MENU/UiId 기반 DB 메타 저장소로 확장 가능.</summary>
public sealed class InMemoryScreenDefinitionProvider : IScreenDefinitionProvider
{
    private readonly ConcurrentDictionary<string, ScreenDefinition> _defs = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryScreenDefinitionProvider()
    {
        // 데모 시드: 메타데이터로 정의한 파라미터 입력 화면(손코딩 .razor 없이 /meta/DEMO_PARAM 으로 렌더)
        Register(new ScreenDefinition("DEMO_PARAM", "데모 — 메타데이터 파라미터", new FieldDefinition[]
        {
            new("parameterId", "파라미터 ID", FieldType.Text, Required: true),
            new("parameterName", "이름", FieldType.Text, Required: true),
            new("unit", "단위", FieldType.Text),
            new("lowerLimit", "하한", FieldType.Number),
            new("upperLimit", "상한", FieldType.Number),
            new("isActive", "활성", FieldType.Boolean),
        }));

        // 데모 시드: 그리드 전용(조회) 화면 — 컬럼 메타 + 파일 기반 명명 쿼리(MDM.PlantList) 바인딩으로
        // /meta/DEMO_GRID 가 손코딩 없이 공장 목록을 렌더한다(저코드 조회 경로 end-to-end 시연).
        Register(new ScreenDefinition("DEMO_GRID", "데모 — 메타데이터 그리드(파일 쿼리)",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PLANT_ID", "공장 ID"),
                new("PLANT_NAME", "공장명"),
                new("COUNTRY", "국가"),
                new("TIME_ZONE", "표준시"),
            },
            QueryId: "MDM.PlantList"));

        // 데모 시드: 폼 저장(쓰기) 화면 — 필드 메타 + 명명 쓰기쿼리(MDM.CreatePlant, kind="write") 바인딩으로
        // /meta/DEMO_PLANT_FORM 폼 저장이 손코딩 없이 공장을 INSERT한다(저장 후 /meta/DEMO_GRID 에서 조회).
        // 필드 Key는 쓰기쿼리의 @param 이름과 일치(plantId/plantName/description/country/timeZone).
        Register(new ScreenDefinition("DEMO_PLANT_FORM", "데모 — 공장 등록(파일 쓰기쿼리)",
            new FieldDefinition[]
            {
                new("plantId", "공장 ID", FieldType.Text, Required: true),
                new("plantName", "공장명", FieldType.Text, Required: true),
                new("description", "설명", FieldType.Text),
                new("country", "국가", FieldType.Text),
                new("timeZone", "표준시", FieldType.Text),
            },
            SaveQueryId: "MDM.CreatePlant"));

        // 데모 시드: 레이아웃(WYSIWYG) 화면 — 좌측 공장 그리드(MDM.PlantList) + 우측 등록 폼/저장 버튼(MDM.CreatePlant)을
        // 한 화면에 조합한다. /meta/DEMO_LAYOUT 이 LayoutRenderer로 렌더되는 레이아웃 런타임 end-to-end 시연.
        Register(new ScreenDefinition("DEMO_LAYOUT", "데모 — 레이아웃(그리드+폼)",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Id = "sec", Title = "공장 마스터",
                Children = new LayoutNode[]
                {
                    new RowNode { Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 7, Children = new LayoutNode[]
                        {
                            new GridWidget { Id = "g", QueryId = "MDM.PlantList", Columns = new GridColumnDefinition[]
                            {
                                new("PLANT_ID", "공장 ID"), new("PLANT_NAME", "공장명"),
                            } },
                        } },
                        new ColumnNode { Span = 5, Children = new LayoutNode[]
                        {
                            new FormWidget { Id = "f", SaveQueryId = "MDM.CreatePlant", Fields = new FieldWidget[]
                            {
                                new() { FieldKey = "plantId", Field = new FieldDefinition("plantId", "공장 ID", FieldType.Text, Required: true) },
                                new() { FieldKey = "plantName", Field = new FieldDefinition("plantName", "공장명", FieldType.Text, Required: true) },
                            } },
                            new ButtonWidget { Id = "b", Label = "저장", Command = "MDM.CreatePlant", RequiredPermission = "mdm:manage" },
                        } },
                    } },
                },
            }));

        // 데모 시드: 그리드 전용(조회) 화면 — QMS 결함분류 목록을 파일 기반 명명 쿼리(QMS.DefectClassList) 바인딩으로
        // /meta/DEMO_QMS_DEFECT_CLASS 가 손코딩 없이 렌더한다(QMS 모듈 저코드 조회 경로 시연).
        Register(new ScreenDefinition("DEMO_QMS_DEFECT_CLASS", "데모 — QMS 결함분류(파일 쿼리)",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("DEFECT_CLASS_ID", "결함분류 ID"),
                new("DEFECT_CLASS_NAME", "결함분류명"),
                new("DESCRIPTION", "설명"),
                new("SEVERITY", "심각도"),
                new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.DefectClassList"));

        // 시스템관리 — 메뉴 관리(CRUD). DEMO_LAYOUT 구조 미러: 좌측 트리 그리드(SYS.MenuTree) + 우측 업서트 폼(SYS.UpsertMenu)·
        // 저장/삭제 명령 버튼(SYS.UpsertMenu/SYS.DeleteMenu). 폼 필드 Key는 쓰기쿼리 @param 이름과 1:1 일치
        // (menuId/menuName/parentMenuId/displaySequence/menuType/uiId). 사이드바의 시스템관리 폴더가 /meta/SYS_MENU_MGMT 로 라우팅.
        Register(new ScreenDefinition("SYS_MENU_MGMT", "메뉴 관리",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Id = "sec", Title = "메뉴 마스터",
                Children = new LayoutNode[]
                {
                    new RowNode { Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 7, Children = new LayoutNode[]
                        {
                            new GridWidget { Id = "g", QueryId = "SYS.MenuTree", Columns = new GridColumnDefinition[]
                            {
                                new("MENU_ID", "메뉴ID"), new("MENU_NAME", "메뉴명"),
                                new("PARENT_MENU_ID", "상위"), new("MENU_TYPE", "유형"), new("UI_ID", "화면"),
                            } },
                        } },
                        new ColumnNode { Span = 5, Children = new LayoutNode[]
                        {
                            new FormWidget { Id = "f", SaveQueryId = "SYS.UpsertMenu", Fields = new FieldWidget[]
                            {
                                new() { FieldKey = "menuId", Field = new FieldDefinition("menuId", "메뉴ID", FieldType.Text, Required: true) },
                                new() { FieldKey = "menuName", Field = new FieldDefinition("menuName", "메뉴명", FieldType.Text, Required: true) },
                                new() { FieldKey = "parentMenuId", Field = new FieldDefinition("parentMenuId", "상위 메뉴ID", FieldType.Text) },
                                new() { FieldKey = "displaySequence", Field = new FieldDefinition("displaySequence", "표시순서", FieldType.Number) },
                                new() { FieldKey = "menuType", Field = new FieldDefinition("menuType", "유형(Folder/Screen)", FieldType.Text) },
                                new() { FieldKey = "uiId", Field = new FieldDefinition("uiId", "화면 UI_ID", FieldType.Text) },
                            } },
                            new ButtonWidget { Id = "bSave", Label = "저장", Command = "SYS.UpsertMenu", RequiredPermission = "sys:manage" },
                            new ButtonWidget { Id = "bDel", Label = "삭제", Command = "SYS.DeleteMenu", RequiredPermission = "sys:manage" },
                        } },
                    } },
                },
            }));

        // ===== SmartUX MDM 업무화면 점등(Phase 2) — 실제 SmartUX 메뉴 잎(menuId=UI_ID)에 기존 명명쿼리를 바인딩한다.
        // 사이드바 MDM 폴더의 해당 잎을 클릭하면 '준비 중' 대신 실제 그리드/폼이 렌더된다. 백엔드(테이블·명명쿼리)가
        // 있는 화면만 점등(나머지는 '준비 중' 유지). =====

        // 공장 관리(FACTORY_MDM_PLANT) — 좌측 공장 그리드(MDM.PlantList) + 우측 등록 폼/저장(MDM.CreatePlant). 실동작 CRUD.
        Register(new ScreenDefinition("FACTORY_MDM_PLANT", "공장 관리",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Id = "sec", Title = "공장 마스터",
                Children = new LayoutNode[]
                {
                    new RowNode { Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 7, Children = new LayoutNode[]
                        {
                            new GridWidget { Id = "g", QueryId = "MDM.PlantList", Columns = new GridColumnDefinition[]
                            {
                                new("PLANT_ID", "공장 ID"), new("PLANT_NAME", "공장명"),
                                new("DESCRIPTION", "설명"), new("COUNTRY", "국가"), new("TIME_ZONE", "표준시"),
                            } },
                        } },
                        new ColumnNode { Span = 5, Children = new LayoutNode[]
                        {
                            new FormWidget { Id = "f", SaveQueryId = "MDM.CreatePlant", Fields = new FieldWidget[]
                            {
                                new() { FieldKey = "plantId", Field = new FieldDefinition("plantId", "공장 ID", FieldType.Text, Required: true) },
                                new() { FieldKey = "plantName", Field = new FieldDefinition("plantName", "공장명", FieldType.Text, Required: true) },
                                new() { FieldKey = "description", Field = new FieldDefinition("description", "설명", FieldType.Text) },
                                new() { FieldKey = "country", Field = new FieldDefinition("country", "국가", FieldType.Text) },
                                new() { FieldKey = "timeZone", Field = new FieldDefinition("timeZone", "표준시", FieldType.Text) },
                            } },
                            new ButtonWidget { Id = "bSave", Label = "저장", Command = "MDM.CreatePlant", RequiredPermission = "mdm:manage" },
                        } },
                    } },
                },
            }));

        // 품목 관리(FACTORY_MDM_ITEM_DEF) — 제품 마스터 조회 그리드(MDM.ProductList). SmartUX '품목' ↔ 새 스키마 MDM_PRODUCT.
        Register(new ScreenDefinition("FACTORY_MDM_ITEM_DEF", "품목 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PRODUCT_ID", "품목 ID"), new("PRODUCT_NAME", "품목명"), new("DESCRIPTION", "설명"),
                new("PRODUCT_TYPE", "유형"), new("UNIT", "단위"), new("VALID_STATE", "상태"),
            },
            QueryId: "MDM.ProductList"));

        // AREA 관리(FACTORY_MDM_AREA) — 구역 마스터 조회 그리드(MDM.AreaList).
        Register(new ScreenDefinition("FACTORY_MDM_AREA", "AREA 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("AREA_ID", "AREA ID"), new("AREA_NAME", "AREA명"),
                new("DESCRIPTION", "설명"), new("PLANT_ID", "공장 ID"),
            },
            QueryId: "MDM.AreaList"));

        // 설비 관리(FACTORY_MDM_EQUIPMENT_DEF) — 설비 마스터 조회 그리드(MDM.EquipmentList, 전체조회 NULL-guard 쿼리).
        Register(new ScreenDefinition("FACTORY_MDM_EQUIPMENT_DEF", "설비 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("EQUIPMENT_NAME", "설비명"),
                new("PLANT_ID", "공장 ID"), new("AREA_ID", "구역 ID"),
                new("EQUIPMENT_TYPE", "설비유형"), new("EQUIPMENT_CLASS_ID", "설비 그룹"), new("VALID_STATE", "상태"),
            },
            QueryId: "MDM.EquipmentList"));

        // 사유 코드 그룹 관리(FACTORY_MDM_REASON_CODE_CLASS) — 코드 클래스 조회 그리드(기존 MDM.CodeClassList).
        Register(new ScreenDefinition("FACTORY_MDM_REASON_CODE_CLASS", "사유 코드 그룹 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("CODE_CLASS_ID", "코드 그룹 ID"), new("CODE_CLASS_NAME", "코드 그룹명"), new("DESCRIPTION", "설명"),
            },
            QueryId: "MDM.CodeClassList"));

        // 사유 코드 관리(FACTORY_MDM_REASON_CODE) — 코드 마스터 조회 그리드(MDM.CodeList, 전체조회 NULL-guard 쿼리).
        Register(new ScreenDefinition("FACTORY_MDM_REASON_CODE", "사유 코드 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("CODE_ID", "코드 ID"), new("CODE_NAME", "코드명"),
                new("CODE_CLASS_ID", "코드 그룹"), new("SORT_ORDER", "정렬"), new("VALID_STATE", "상태"),
            },
            QueryId: "MDM.CodeList"));

        // ===== SmartUX QMS 업무화면 점등(Phase 2) — 실존 QMS 명명쿼리·테이블이 있는 마스터 화면만 점등. =====

        // 검사 SPEC 관리(QMS_STD_INSP_SPEC) — 검사 규격 마스터 조회 그리드(QMS.InspectionSpecList).
        Register(new ScreenDefinition("QMS_STD_INSP_SPEC", "검사 SPEC 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("SPEC_ID", "규격 ID"), new("SPEC_NAME", "규격명"), new("PROCESS_ID", "공정 ID"),
                new("ITEM_NAME", "품목명"), new("MEASURE_TYPE", "측정유형"),
                new("NOMINAL_VALUE", "공칭값"), new("TOLERANCE_PLUS", "상한공차"), new("TOLERANCE_MINUS", "하한공차"),
                new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.InspectionSpecList"));

        // SPC 관리도(QMS_SPC_CONTROL_CHART) — SPC 파라미터(관리한계) 조회 그리드(QMS.SpcParamList).
        Register(new ScreenDefinition("QMS_SPC_CONTROL_CHART", "SPC 관리도",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PARAM_ID", "파라미터 ID"), new("PARAM_NAME", "파라미터명"),
                new("EQUIPMENT_ID", "설비 ID"), new("PROCESS_ID", "공정 ID"),
                new("MEAN", "평균"), new("UCL", "관리상한"), new("LCL", "관리하한"),
                new("SAMPLE_SIZE", "표본수"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.SpcParamList"));

        // ===== SmartUX MDM 잔여 업무화면 점등(V035 신설 테이블 백엔드) — 12종 조회 그리드. =====
        Register(new ScreenDefinition("FACTORY_MDM_EQUIPMENT_CLASS", "설비 그룹 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("EQUIPMENT_CLASS_ID", "설비 그룹 ID"), new("EQUIPMENT_CLASS_NAME", "설비 그룹명"), new("DESCRIPTION", "설명") },
            QueryId: "MDM.EquipmentClassList"));
        Register(new ScreenDefinition("FACTORY_MDM_ITEM_CLASS", "품목 그룹 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("ITEM_CLASS_ID", "품목 그룹 ID"), new("ITEM_CLASS_NAME", "품목 그룹명"), new("DESCRIPTION", "설명") },
            QueryId: "MDM.ItemClassList"));
        Register(new ScreenDefinition("FACTORY_MDM_CARRIER_CLASS", "캐리어 그룹 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("CARRIER_CLASS_ID", "캐리어 그룹 ID"), new("CARRIER_CLASS_NAME", "캐리어 그룹명"), new("DESCRIPTION", "설명") },
            QueryId: "MDM.CarrierClassList"));
        Register(new ScreenDefinition("FACTORY_MDM_CARRIER", "캐리어 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("CARRIER_ID", "캐리어 ID"), new("CARRIER_NAME", "캐리어명"), new("CARRIER_CLASS_ID", "캐리어 그룹"), new("DESCRIPTION", "설명") },
            QueryId: "MDM.CarrierList"));
        Register(new ScreenDefinition("FACTORY_MDM_SEGMENT_CLASS", "공정 그룹 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("SEGMENT_CLASS_ID", "공정 그룹 ID"), new("SEGMENT_CLASS_NAME", "공정 그룹명"), new("DESCRIPTION", "설명") },
            QueryId: "MDM.SegmentClassList"));
        Register(new ScreenDefinition("FACTORY_MDM_SEGMENT_DEF", "공정 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("SEGMENT_ID", "공정 ID"), new("SEGMENT_NAME", "공정명"), new("SEGMENT_CLASS_ID", "공정 그룹"), new("DESCRIPTION", "설명") },
            QueryId: "MDM.SegmentList"));
        Register(new ScreenDefinition("FACTORY_MDM_PROCESS_CLASS", "프로세스 그룹 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("PROCESS_CLASS_ID", "프로세스 그룹 ID"), new("PROCESS_CLASS_NAME", "프로세스 그룹명"), new("DESCRIPTION", "설명") },
            QueryId: "MDM.ProcessClassList"));
        Register(new ScreenDefinition("FACTORY_MDM_PROCESS_DEF", "프로세스 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("PROCESS_ID", "프로세스 ID"), new("PROCESS_NAME", "프로세스명"), new("PROCESS_CLASS_ID", "프로세스 그룹"), new("DESCRIPTION", "설명") },
            QueryId: "MDM.ProcessList"));
        Register(new ScreenDefinition("FACTORY_MDM_PROCESS_PATH", "라우팅 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("ROUTING_ID", "라우팅 ID"), new("ROUTING_NAME", "라우팅명"), new("PRODUCT_ID", "품목 ID"), new("DESCRIPTION", "설명") },
            QueryId: "MDM.RoutingList"));
        Register(new ScreenDefinition("FACTORY_MDM_BILL_OF_MATERIAL", "BOM 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("BOM_ID", "BOM ID"), new("PRODUCT_ID", "제품 ID"), new("COMPONENT_ID", "부품 ID"), new("QUANTITY", "수량") },
            QueryId: "MDM.BomList"));
        Register(new ScreenDefinition("FACTORY_MDM_SEGMENT_QTIME", "Qtime 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("QTIME_ID", "Qtime ID"), new("SEGMENT_ID", "공정 ID"), new("STANDARD_TIME", "표준시간"), new("UNIT", "단위") },
            QueryId: "MDM.QtimeList"));
        Register(new ScreenDefinition("FACTORY_MDM_QTIME_ACTION", "Qtime 액션 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("ACTION_ID", "액션 ID"), new("QTIME_ID", "Qtime ID"), new("ACTION_CODE", "액션코드"), new("DESCRIPTION", "설명") },
            QueryId: "MDM.QtimeActionList"));

        // ===== SmartUX EMS(EMS) 업무화면 점등(Phase 2) — 보전 read 슬라이스(예비품/작업지시/보전계획) 마스터 조회.
        // 메뉴 접두사 EMS = C# 모듈 EMS. 예비품은 기존 무파라미터 쿼리(SparePartsAll), 작업지시/보전계획은
        // 신규 NULL-guard 전체조회 쿼리(EMS.WorkOrderList/MaintenancePlanList). 그리드 read는 형제와 동일하게 인증만(권한 무). =====

        // Spare Part 관리(FACTORY_EMS_STD_SPARE_PART) — 예비품 마스터 조회(EMS.SparePartsAll, 무파라미터 전체조회).
        Register(new ScreenDefinition("FACTORY_EMS_STD_SPARE_PART", "Spare Part 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PART_ID", "부품 ID"), new("PART_NAME", "부품명"), new("PART_NUMBER", "부품번호"),
                new("UNIT_OF_MEASURE", "단위"), new("CURRENT_STOCK", "현재고"), new("MIN_STOCK", "최소재고"),
                new("MAX_STOCK", "최대재고"), new("LOCATION", "위치"), new("EQUIPMENT_CLASS_ID", "설비 그룹"),
            },
            QueryId: "EMS.SparePartsAll"));

        // Spare Part 재고 조회(FACTORY_EMS_STD_SPARE_PART_STOCK) — 동일 마스터를 재고 중심 컬럼으로 조회.
        Register(new ScreenDefinition("FACTORY_EMS_STD_SPARE_PART_STOCK", "Spare Part 재고 조회",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PART_ID", "부품 ID"), new("PART_NAME", "부품명"), new("PART_NUMBER", "부품번호"),
                new("CURRENT_STOCK", "현재고"), new("MIN_STOCK", "최소재고"), new("MAX_STOCK", "최대재고"), new("LOCATION", "위치"),
            },
            QueryId: "EMS.SparePartsAll"));

        // 설비 보전 결과(FACTORY_EMS_BM_ORDER_RESULT) — 작업지시 전체조회(EMS.WorkOrderList, NULL-guard).
        Register(new ScreenDefinition("FACTORY_EMS_BM_ORDER_RESULT", "설비 보전 결과",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("WO_ID", "작업지시 ID"), new("EQUIPMENT_ID", "설비 ID"), new("WO_TYPE", "유형"),
                new("DESCRIPTION", "설명"), new("ASSIGNEE_ID", "담당자"), new("ISSUED_AT", "발행일시"),
                new("COMPLETED_AT", "완료일시"), new("STATUS", "상태"),
            },
            QueryId: "EMS.WorkOrderList"));

        // 설비 수리 요청 그리드(FACTORY_EMS_BM_ORDER_GRIDTYPE) — 작업지시 그리드(동일 EMS.WorkOrderList).
        Register(new ScreenDefinition("FACTORY_EMS_BM_ORDER_GRIDTYPE", "설비 수리 요청 그리드",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("WO_ID", "작업지시 ID"), new("EQUIPMENT_ID", "설비 ID"), new("WO_TYPE", "유형"),
                new("DESCRIPTION", "설명"), new("ASSIGNEE_ID", "담당자"), new("ISSUED_AT", "발행일시"),
                new("STARTED_AT", "착수일시"), new("STATUS", "상태"),
            },
            QueryId: "EMS.WorkOrderList"));

        // PM 계획 관리(FACTORY_EMS_PM_ORDER_PLAN) — 보전계획 전체조회(EMS.MaintenancePlanList, NULL-guard).
        Register(new ScreenDefinition("FACTORY_EMS_PM_ORDER_PLAN", "PM 계획 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PLAN_ID", "계획 ID"), new("PLAN_NAME", "계획명"), new("EQUIPMENT_ID", "설비 ID"),
                new("PLAN_TYPE", "유형"), new("CYCLE_TYPE", "주기"), new("SCHEDULED_DATE", "예정일"),
                new("ESTIMATED_DURATION_HOURS", "예상시간(h)"), new("ASSIGNEE_ID", "담당자"), new("STATUS", "상태"),
            },
            QueryId: "EMS.MaintenancePlanList"));

        // PM 계획 그리드(FACTORY_EMS_PM_ORDER_PLAN_GRIDTYPE) — 보전계획 그리드(동일 EMS.MaintenancePlanList).
        Register(new ScreenDefinition("FACTORY_EMS_PM_ORDER_PLAN_GRIDTYPE", "PM 계획 그리드",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PLAN_ID", "계획 ID"), new("PLAN_NAME", "계획명"), new("EQUIPMENT_ID", "설비 ID"),
                new("PLAN_TYPE", "유형"), new("CYCLE_TYPE", "주기"), new("SCHEDULED_DATE", "예정일"),
                new("ASSIGNEE_ID", "담당자"), new("STATUS", "상태"),
            },
            QueryId: "EMS.MaintenancePlanList"));

        // ===== SmartUX FDC(EES_FDC) 업무화면 점등(Phase 2) — 설정/이력 read 슬라이스(파라미터그룹/파라미터/인터락이력).
        // 메뉴 접두사 EES_FDC = C# 모듈 FDC. 기존 쿼리는 모두 @equipmentId 필수라, 점등용 NULL-guard 전체조회
        // 쿼리(FDC.ParameterGroupList/ParameterList/InterlockHistoryList)를 신설해 바인딩. 그리드 read는 형제와 동일하게 인증만. =====

        // 파라미터 수집 그룹 관리(EES_FDC_TRACE_GROUP) — 파라미터 그룹 마스터 조회(FDC.ParameterGroupList).
        Register(new ScreenDefinition("EES_FDC_TRACE_GROUP", "파라미터 수집 그룹 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("GROUP_ID", "그룹 ID"), new("GROUP_NAME", "그룹명"), new("EQUIPMENT_ID", "설비 ID"),
                new("DESCRIPTION", "설명"), new("DISPLAY_ORDER", "표시순서"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "FDC.ParameterGroupList"));

        // TRACE 파라미터 관리(EES_FDC_TRACE_PARAMETER_MANAGEMENT) — 수집 파라미터 마스터 조회(FDC.ParameterList).
        Register(new ScreenDefinition("EES_FDC_TRACE_PARAMETER_MANAGEMENT", "TRACE 파라미터 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PARAMETER_ID", "파라미터 ID"), new("PARAMETER_NAME", "파라미터명"), new("EQUIPMENT_ID", "설비 ID"),
                new("GROUP_ID", "그룹 ID"), new("UNIT", "단위"), new("SAMPLING_INTERVAL_MS", "수집주기(ms)"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "FDC.ParameterList"));

        // ACTIVE 파라미터 스펙 관리(EES_FDC_ACTIVE_SPEC_MANAGEMENT) — 동일 파라미터 마스터를 스펙(관리한도) 중심 컬럼으로 조회.
        Register(new ScreenDefinition("EES_FDC_ACTIVE_SPEC_MANAGEMENT", "ACTIVE 파라미터 스펙 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PARAMETER_ID", "파라미터 ID"), new("PARAMETER_NAME", "파라미터명"), new("UNIT", "단위"),
                new("LOWER_LIMIT", "하한"), new("UPPER_LIMIT", "상한"),
                new("LOWER_CONTROL_LIMIT", "관리하한"), new("UPPER_CONTROL_LIMIT", "관리상한"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "FDC.ParameterList"));

        // 인터락 이력 조회(EES_FDC_INTERLOCK_HISTORY) — 인터락 발동/해제 이력 조회(FDC.InterlockHistoryList).
        Register(new ScreenDefinition("EES_FDC_INTERLOCK_HISTORY", "인터락 이력 조회",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("HISTORY_ID", "이력 ID"), new("RULE_ID", "규칙 ID"), new("EQUIPMENT_ID", "설비 ID"),
                new("PARAMETER_ID", "파라미터 ID"), new("TRIGGER_VALUE", "발동값"), new("ACTION", "조치"),
                new("MESSAGE", "메시지"), new("TRIGGERED_AT", "발동시각"), new("RESOLVED_AT", "해제시각"), new("IS_RESOLVED", "해제"),
            },
            QueryId: "FDC.InterlockHistoryList"));

        // ===== SmartUX FDC(EES_FDC) 업무화면 점등(Phase 3) — 수집 데이터 차트/인터락 규칙/파라미터 뷰 확장.
        // FDC 데이터 차트류는 수집 시계열(FDC.CollectDataList) 그리드로, 파라미터 관리/스펙류는 단일 파라미터
        // 마스터(FDC.ParameterList)를 컬럼 레이아웃만 달리해 재사용(FDC_PARAMETER에 타입 구분 컬럼 부재 — 형제 ACTIVE/TRACE와 동일 패턴). =====

        // FDC 데이터 차트(EES_FDC_DATA_CHART) — 수집 시계열 최근값 조회(FDC.CollectDataList).
        Register(new ScreenDefinition("EES_FDC_DATA_CHART", "FDC 데이터 차트",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("PARAMETER_ID", "파라미터 ID"), new("VALUE", "측정값"),
                new("COLLECTED_AT", "수집시각"), new("QUALITY", "품질"), new("LOWER_LIMIT", "하한"), new("UPPER_LIMIT", "상한"),
            },
            QueryId: "FDC.CollectDataList"));

        // FDC 관심 데이터 차트(EES_FDC_INTERESTED_DATA_CHART) — 동일 수집 시계열(관심 파라미터 뷰).
        Register(new ScreenDefinition("EES_FDC_INTERESTED_DATA_CHART", "FDC 관심 데이터 차트",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("PARAMETER_ID", "파라미터 ID"), new("VALUE", "측정값"),
                new("COLLECTED_AT", "수집시각"), new("QUALITY", "품질"), new("LOWER_LIMIT", "하한"), new("UPPER_LIMIT", "상한"),
            },
            QueryId: "FDC.CollectDataList"));

        // 실시간 데이터 차트(EES_FDC_REAL_TIME_TRACE_PARA_MONITORING) — 수집 시계열 최근값(실시간 모니터링).
        Register(new ScreenDefinition("EES_FDC_REAL_TIME_TRACE_PARA_MONITORING", "실시간 데이터 차트",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("PARAMETER_ID", "파라미터 ID"), new("VALUE", "측정값"),
                new("COLLECTED_AT", "수집시각"), new("QUALITY", "품질"), new("LOWER_LIMIT", "하한"), new("UPPER_LIMIT", "상한"),
            },
            QueryId: "FDC.CollectDataList"));

        // FDC SUMMARY 데이터 차트(EES_FDC_SUMMARY_DATA_CHART) — 수집 시계열 요약 뷰.
        Register(new ScreenDefinition("EES_FDC_SUMMARY_DATA_CHART", "FDC SUMMARY 데이터 차트",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("PARAMETER_ID", "파라미터 ID"), new("VALUE", "측정값"),
                new("COLLECTED_AT", "수집시각"), new("QUALITY", "품질"), new("LOWER_LIMIT", "하한"), new("UPPER_LIMIT", "상한"),
            },
            QueryId: "FDC.CollectDataList"));

        // 파라미터별 설비 상태 변경 관리(EES_FDC_PARAMETER_STATE_CONDITION) — 인터락 규칙(파라미터 조건→조치) 조회(FDC.InterlockRuleList).
        Register(new ScreenDefinition("EES_FDC_PARAMETER_STATE_CONDITION", "파라미터별 설비 상태 변경 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("RULE_ID", "규칙 ID"), new("RULE_NAME", "규칙명"), new("EQUIPMENT_ID", "설비 ID"),
                new("PARAMETER_ID", "파라미터 ID"), new("OPERATOR", "연산자"), new("THRESHOLD_VALUE", "임계값"),
                new("ACTION", "조치"), new("PRIORITY", "우선순위"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "FDC.InterlockRuleList"));

        // 파라미터 관리 뷰(EVENT/INTERESTED/SUMMARY/VIRTUAL) — 단일 파라미터 마스터(FDC.ParameterList) 재사용(마스터 레이아웃).
        Register(new ScreenDefinition("EES_FDC_EVENT_PARAMETER_MANAGEMENT", "EVENT 파라미터 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PARAMETER_ID", "파라미터 ID"), new("PARAMETER_NAME", "파라미터명"), new("EQUIPMENT_ID", "설비 ID"),
                new("GROUP_ID", "그룹 ID"), new("UNIT", "단위"), new("SAMPLING_INTERVAL_MS", "수집주기(ms)"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "FDC.ParameterList"));
        Register(new ScreenDefinition("EES_FDC_INTERESTED_PARAMETER_MANAGEMENT", "관심 파라미터 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PARAMETER_ID", "파라미터 ID"), new("PARAMETER_NAME", "파라미터명"), new("EQUIPMENT_ID", "설비 ID"),
                new("GROUP_ID", "그룹 ID"), new("UNIT", "단위"), new("SAMPLING_INTERVAL_MS", "수집주기(ms)"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "FDC.ParameterList"));
        Register(new ScreenDefinition("EES_FDC_SUMMARY_PARAMETER_MANAGEMENT", "SUMMARY 파라미터 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PARAMETER_ID", "파라미터 ID"), new("PARAMETER_NAME", "파라미터명"), new("EQUIPMENT_ID", "설비 ID"),
                new("GROUP_ID", "그룹 ID"), new("UNIT", "단위"), new("SAMPLING_INTERVAL_MS", "수집주기(ms)"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "FDC.ParameterList"));
        Register(new ScreenDefinition("EES_FDC_VIRTUAL_PARAMETER_MANAGEMENT", "VIRTUAL 파라미터 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PARAMETER_ID", "파라미터 ID"), new("PARAMETER_NAME", "파라미터명"), new("EQUIPMENT_ID", "설비 ID"),
                new("GROUP_ID", "그룹 ID"), new("UNIT", "단위"), new("SAMPLING_INTERVAL_MS", "수집주기(ms)"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "FDC.ParameterList"));

        // 파라미터 스펙 뷰(IDLE/SUMMARY) — 동일 파라미터 마스터를 스펙(관리한도) 컬럼으로 조회(형제 ACTIVE_SPEC와 동일).
        Register(new ScreenDefinition("EES_FDC_IDLE_SPEC_MANAGEMENT", "IDLE 파라미터 스펙 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PARAMETER_ID", "파라미터 ID"), new("PARAMETER_NAME", "파라미터명"), new("UNIT", "단위"),
                new("LOWER_LIMIT", "하한"), new("UPPER_LIMIT", "상한"),
                new("LOWER_CONTROL_LIMIT", "관리하한"), new("UPPER_CONTROL_LIMIT", "관리상한"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "FDC.ParameterList"));
        Register(new ScreenDefinition("EES_FDC_SUMMARY_SPEC_MANAGEMENT", "SUMMARY 파라미터 스펙 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PARAMETER_ID", "파라미터 ID"), new("PARAMETER_NAME", "파라미터명"), new("UNIT", "단위"),
                new("LOWER_LIMIT", "하한"), new("UPPER_LIMIT", "상한"),
                new("LOWER_CONTROL_LIMIT", "관리하한"), new("UPPER_CONTROL_LIMIT", "관리상한"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "FDC.ParameterList"));

        // ===== SmartUX EPT(EES_EPT) 업무화면 점등(Phase 3) — 설비 성능관리. 메뉴 접두사 EES_EPT = C# 모듈 EST(설비 상태 추적).
        // 현재상태(EST.CurrentStateList)·상태이력(EST.StateHistoryList)·설비알람(EST.EquipmentAlarmList)·WORST10 집계
        // (EST.WorstAlarmEquipment)를 신설해 바인딩. OEE/유실분석/관심지표/레이아웃은 산출 데이터 모델 부재로 보류. =====

        // 설비 상태 현황(EES_EPT_EQUIPMENT_STATE_STATUS) — 설비별 현재 상태(EST.CurrentStateList).
        Register(new ScreenDefinition("EES_EPT_EQUIPMENT_STATE_STATUS", "설비 상태 현황",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("PLANT_ID", "공장"), new("CURRENT_STATE_ID", "현재 상태"),
                new("STATE_CHANGED_AT", "상태변경시각"), new("STATE_VERSION", "버전"),
            },
            QueryId: "EST.CurrentStateList"));

        // 공장 모니터링(EES_EPT_PLANT_MONITORING) — 공장 단위 설비 현재 상태 현황(동일 현재상태 뷰).
        Register(new ScreenDefinition("EES_EPT_PLANT_MONITORING", "공장 모니터링",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PLANT_ID", "공장"), new("EQUIPMENT_ID", "설비 ID"), new("CURRENT_STATE_ID", "현재 상태"),
                new("STATE_CHANGED_AT", "상태변경시각"), new("STATE_VERSION", "버전"),
            },
            QueryId: "EST.CurrentStateList"));

        // 설비 상태 변경(EES_EPT_CHANGE_EQUIPMENT_STATE) — 현재 상태 조회 그리드(변경 조작은 EST 브리지 소관, 조회 점등).
        Register(new ScreenDefinition("EES_EPT_CHANGE_EQUIPMENT_STATE", "설비 상태 변경",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("PLANT_ID", "공장"), new("CURRENT_STATE_ID", "현재 상태"),
                new("STATE_CHANGED_AT", "상태변경시각"), new("STATE_VERSION", "버전"),
            },
            QueryId: "EST.CurrentStateList"));

        // 설비 상태 이력(EES_EPT_EQUIPMENT_STATE_HISTORY) — 상태 변경 이력(EST.StateHistoryList).
        Register(new ScreenDefinition("EES_EPT_EQUIPMENT_STATE_HISTORY", "설비 상태 이력",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("FROM_STATE", "이전 상태"), new("TO_STATE", "변경 상태"),
                new("SET_STATE", "설정 상태"), new("CHANGED_AT", "변경시각"), new("CHANGED_BY", "변경자"),
                new("REASON", "사유"), new("SOURCE_TYPE", "출처"),
            },
            QueryId: "EST.StateHistoryList"));

        // 설비 이벤트 이력(EES_EPT_EQUIPMENT_EVENT_HISTORY) — 상태 전이를 이벤트 로그로 조회(동일 상태이력 뷰).
        Register(new ScreenDefinition("EES_EPT_EQUIPMENT_EVENT_HISTORY", "설비 이벤트 이력",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("FROM_STATE", "이전 상태"), new("TO_STATE", "변경 상태"),
                new("CHANGED_AT", "발생시각"), new("CHANGED_BY", "발생자"), new("SOURCE_TYPE", "출처"), new("REASON", "사유"),
            },
            QueryId: "EST.StateHistoryList"));

        // 설비 가동 이력(EES_EPT_EQUIPMENT_PRODUCTIVE_HISTORY) — 상태 변경 이력을 가동 관점으로 조회(동일 상태이력 뷰).
        Register(new ScreenDefinition("EES_EPT_EQUIPMENT_PRODUCTIVE_HISTORY", "설비 가동 이력",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("TO_STATE", "가동 상태"), new("CHANGED_AT", "변경시각"),
                new("CHANGED_BY", "변경자"), new("SOURCE_TYPE", "출처"), new("REASON", "사유"),
            },
            QueryId: "EST.StateHistoryList"));

        // 설비 알람 이력(EES_EPT_EQUIPMENT_ALARM_HISTORY) — 설비 알람(EST.EquipmentAlarmList).
        Register(new ScreenDefinition("EES_EPT_EQUIPMENT_ALARM_HISTORY", "설비 알람 이력",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("ALARM_CODE", "알람 코드"), new("ALARM_NAME", "알람명"),
                new("ALARM_LEVEL", "등급"), new("OCCURRED_AT", "발생시각"), new("CLEARED_AT", "해제시각"), new("ELAPSED_SECONDS", "지속(초)"),
            },
            QueryId: "EST.EquipmentAlarmList"));

        // 알람 발생 이력(EES_EPT_ALARM_HISTORY) — 동일 설비 알람 이력 뷰.
        Register(new ScreenDefinition("EES_EPT_ALARM_HISTORY", "알람 발생 이력",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("ALARM_CODE", "알람 코드"), new("ALARM_NAME", "알람명"),
                new("ALARM_LEVEL", "등급"), new("OCCURRED_AT", "발생시각"), new("CLEARED_AT", "해제시각"), new("ELAPSED_SECONDS", "지속(초)"),
            },
            QueryId: "EST.EquipmentAlarmList"));

        // WORST10 알람(EES_EPT_WORST10_ALARM) — 설비별 알람 발생 건수 상위 10(EST.WorstAlarmEquipment 집계).
        Register(new ScreenDefinition("EES_EPT_WORST10_ALARM", "WORST10 알람",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("ALARM_COUNT", "알람 건수"), new("LAST_OCCURRED_AT", "최근 발생시각"),
            },
            QueryId: "EST.WorstAlarmEquipment"));

        // ===== SmartUX EPT OEE(설비종합효율) 점등(Phase 4) — V050 마트. OEE=가용성×성능×품질. 사전집계 마트 read
        // (원자료→마트 집계는 배치/워커 소관, 후속). 비율 컬럼(AVAILABILITY/PERFORMANCE/QUALITY/OEE)은 분율(0~1). =====

        // 설비 종합 지표(EES_EPT_OVERALL_EQUIPMENT_EFFECIVENESS) — 설비×일자 OEE 마트(EST.OeeSummaryList).
        Register(new ScreenDefinition("EES_EPT_OVERALL_EQUIPMENT_EFFECIVENESS", "설비 종합 지표(OEE)",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("OEE_DATE", "일자"), new("EQUIPMENT_ID", "설비 ID"), new("PLANT_ID", "공장"),
                new("AVAILABILITY", "가용성"), new("PERFORMANCE", "성능"), new("QUALITY", "품질"), new("OEE", "OEE"),
                new("PLANNED_MINUTES", "계획(분)"), new("DOWNTIME_MINUTES", "비가동(분)"),
                new("TOTAL_COUNT", "총생산"), new("GOOD_COUNT", "양품"),
            },
            QueryId: "EST.OeeSummaryList"));

        // 설비 유실 분석(EES_EPT_EQUIPMENT_LOSS_ANALYSIS) — 6대 손실 카테고리별 손실 집계(EST.LossByCategory).
        Register(new ScreenDefinition("EES_EPT_EQUIPMENT_LOSS_ANALYSIS", "설비 유실 분석",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("LOSS_CATEGORY", "손실 유형"), new("LOSS_COUNT", "발생 건수"), new("TOTAL_MINUTES", "총 손실(분)"),
            },
            QueryId: "EST.LossByCategory"));

        // WORST5 유실(EES_EPT_WORST5_LOSS) — 설비별 총 손실 시간 상위 5(EST.WorstLossEquipment 집계).
        Register(new ScreenDefinition("EES_EPT_WORST5_LOSS", "WORST5 유실",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("TOTAL_MINUTES", "총 손실(분)"), new("LOSS_COUNT", "손실 건수"),
            },
            QueryId: "EST.WorstLossEquipment"));

        // 관심 지표 등록(EES_EPT_INTERESTED_INDEX_MANAGEMENT) — KPI 지표 마스터(EST.IndexList).
        Register(new ScreenDefinition("EES_EPT_INTERESTED_INDEX_MANAGEMENT", "관심 지표 등록",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("INDEX_ID", "지표 ID"), new("INDEX_NAME", "지표명"), new("INDEX_CATEGORY", "분류"),
                new("UNIT", "단위"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "EST.IndexList"));

        // 지표 관리(EPT_STD_INDEX_MGNT) — 동일 KPI 지표 마스터 뷰.
        Register(new ScreenDefinition("EPT_STD_INDEX_MGNT", "지표 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("INDEX_ID", "지표 ID"), new("INDEX_NAME", "지표명"), new("INDEX_CATEGORY", "분류"),
                new("UNIT", "단위"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "EST.IndexList"));

        // 관심 지표 조회(EES_EPT_INTERESTED_INDEX_VIEW) — 지표×설비×일자 측정값(EST.IndexValueList).
        Register(new ScreenDefinition("EES_EPT_INTERESTED_INDEX_VIEW", "관심 지표 조회",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("OEE_DATE", "일자"), new("INDEX_ID", "지표 ID"), new("EQUIPMENT_ID", "설비 ID"),
                new("PLANT_ID", "공장"), new("SHIFT_ID", "작업조"), new("INDEX_VALUE", "값"),
            },
            QueryId: "EST.IndexValueList"));

        // ===== SmartUX POM(PPM)·SHP(DLV) 업무화면 점등(Phase 2) — 생산오더/출하지시·출하이력 조회.
        // 메뉴 접두사 PPM=POM, DLV=SHP. 기존 쿼리는 필수 @param이라 점등용 NULL-guard 전체조회 쿼리 신설.
        // POM W/O(작업지시) 화면은 전용 테이블 부재로 보류(POM_PRODUCTION_ORDER=생산오더만). 그리드 read는 형제와 동일하게 인증만. =====

        // P/O 관리(FACTORY_PPM_PRODUCTION_ORDER) — 생산오더 조회(POM.ProductionOrderList).
        Register(new ScreenDefinition("FACTORY_PPM_PRODUCTION_ORDER", "P/O 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ORDER_ID", "오더 ID"), new("PLAN_ID", "계획 ID"), new("EQUIPMENT_ID", "설비 ID"),
                new("PRODUCT_ID", "품목 ID"), new("ORDER_QTY", "지시수량"), new("ACTUAL_QTY", "실적수량"),
                new("SCHEDULED_START", "예정시작"), new("SCHEDULED_END", "예정종료"), new("STATUS", "상태"),
            },
            QueryId: "POM.ProductionOrderList"));

        // 생산지시 현황(FACTORY_PPM_REPORT_PRODUCTIONORDER) — 동일 생산오더를 현황(실적·일정) 중심으로 조회.
        Register(new ScreenDefinition("FACTORY_PPM_REPORT_PRODUCTIONORDER", "생산지시 현황",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ORDER_ID", "오더 ID"), new("PRODUCT_ID", "품목 ID"), new("ORDER_QTY", "지시수량"),
                new("ACTUAL_QTY", "실적수량"), new("SCHEDULED_START", "예정시작"), new("ACTUAL_START", "실적시작"),
                new("ACTUAL_END", "실적종료"), new("STATUS", "상태"),
            },
            QueryId: "POM.ProductionOrderList"));

        // ===== SmartUX WPM(작업진행)·RPT·MDM_COM 점등 — 기존 테이블(POM_LOT/POM_LOT_HISTORY/MDM_SHIFT)만 사용, 마이그레이션 0. =====

        // LOT 관리(FACTORY_WPM_LOT_MANAGEMENT) — 전체 Lot 조회(POM.LotList).
        Register(new ScreenDefinition("FACTORY_WPM_LOT_MANAGEMENT", "LOT 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("LOT_ID", "LOT ID"), new("PLANT_ID", "공장"), new("PRODUCT_ID", "품목"), new("QTY", "수량"),
                new("DEFECT_QTY", "불량수량"), new("LOT_STATE", "LOT상태"), new("PROCESS_STATE", "공정상태"),
                new("CURRENT_STEP", "현재스텝"), new("EQUIPMENT_ID", "설비"), new("IS_HOLD", "홀드"),
            },
            QueryId: "POM.LotList"));

        // LOT Hold(FACTORY_WPM_LOT_HOLD)·Hold 해제(FACTORY_WPM_LOT_HOLD_RELEASE) — 홀드 상태 Lot 조회(POM.LotHoldList).
        Register(new ScreenDefinition("FACTORY_WPM_LOT_HOLD", "LOT Hold",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("LOT_ID", "LOT ID"), new("PLANT_ID", "공장"), new("PRODUCT_ID", "품목"), new("QTY", "수량"),
                new("LOT_STATE", "LOT상태"), new("PROCESS_STATE", "공정상태"), new("EQUIPMENT_ID", "설비"), new("IS_HOLD", "홀드"),
            },
            QueryId: "POM.LotHoldList"));
        Register(new ScreenDefinition("FACTORY_WPM_LOT_HOLD_RELEASE", "LOT Hold 해제",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("LOT_ID", "LOT ID"), new("PLANT_ID", "공장"), new("PRODUCT_ID", "품목"), new("QTY", "수량"),
                new("LOT_STATE", "LOT상태"), new("PROCESS_STATE", "공정상태"), new("EQUIPMENT_ID", "설비"), new("IS_HOLD", "홀드"),
            },
            QueryId: "POM.LotHoldList"));

        // 불량 수리(FACTORY_WPM_DEFECT_REPAIR) — 불량 수량 존재 Lot 조회(POM.LotDefectList).
        Register(new ScreenDefinition("FACTORY_WPM_DEFECT_REPAIR", "불량 수리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("LOT_ID", "LOT ID"), new("PLANT_ID", "공장"), new("PRODUCT_ID", "품목"),
                new("QTY", "수량"), new("DEFECT_QTY", "불량수량"), new("LOT_STATE", "LOT상태"), new("EQUIPMENT_ID", "설비"),
            },
            QueryId: "POM.LotDefectList"));

        // 수율 현황(FACTORY_WPM_REPORT_YIELD_STATUS) — 품목별 생산/불량/양품 집계(POM.YieldByProduct).
        Register(new ScreenDefinition("FACTORY_WPM_REPORT_YIELD_STATUS", "수율 현황",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PRODUCT_ID", "품목"), new("LOT_COUNT", "LOT수"), new("TOTAL_QTY", "총생산"),
                new("DEFECT_QTY", "불량"), new("GOOD_QTY", "양품"),
            },
            QueryId: "POM.YieldByProduct"));

        // LOT 추적(FACTORY_RPT_LOT_TRACE) — Lot 이력 조회(POM.LotTraceList).
        Register(new ScreenDefinition("FACTORY_RPT_LOT_TRACE", "LOT 추적",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("LOT_ID", "LOT ID"), new("PLANT_ID", "공장"), new("EQUIPMENT_ID", "설비"), new("PROCESS_ID", "공정"),
                new("TRACK_IN_TIME", "In시각"), new("TRACK_OUT_TIME", "Out시각"), new("EXECUTION_ID", "실행"),
                new("QTY", "수량"), new("DEFECT_QTY", "불량"), new("LOT_STATE", "LOT상태"),
            },
            QueryId: "POM.LotTraceList"));

        // 작업조 관리(MES_MDM_COM_SHIFT) — 작업조 마스터 조회(MDM.ShiftList, 기존 쿼리 재사용).
        Register(new ScreenDefinition("MES_MDM_COM_SHIFT", "작업조 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("SHIFT_ID", "작업조 ID"), new("SHIFT_NAME", "작업조명"), new("START_TIME", "시작"),
                new("END_TIME", "종료"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "MDM.ShiftList"));

        // ===== SmartUX FACTORY_QCA(품질검사) 점등 — 기존 QMS 검사 도메인(V037/V040)으로 전수 재사용, 마이그레이션 0.
        // FACTORY_QCA는 QMS 검사(수입/공정/출하·정의·항목·방법·규격)로 향하는 다른 메뉴 경로다. =====
        RegisterQcaInspection("FACTORY_QCA_IMPORT_INSPECTION", "수입검사 관리", "QMS.IncomingInspectionList");
        RegisterQcaInspection("FACTORY_QCA_REPORT_IMPORT_INSPECTION_STATUS", "수입검사 현황", "QMS.IncomingInspectionList");
        RegisterQcaInspection("FACTORY_QCA_SEGMENT_INSPECTION", "공정검사 관리", "QMS.ProcessInspectionList");
        RegisterQcaInspection("FACTORY_QCA_REPORT_SEGMENT_INSPECTION_STATUS", "공정검사 현황", "QMS.ProcessInspectionList");
        RegisterQcaInspection("FACTORY_QCA_DELIVERY_INSPECTION", "출하검사 관리", "QMS.ShippingInspectionList");
        RegisterQcaInspection("FACTORY_QCA_REPORT_DELIVERY_INSPECTION_STATUS", "출하검사 현황", "QMS.ShippingInspectionList");

        // 검사 정의(FACTORY_QCA_INSPECTION_CLASS) — 검사 정의 마스터(QMS.InspectionDefList).
        Register(new ScreenDefinition("FACTORY_QCA_INSPECTION_CLASS", "검사 정의",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("INSP_DEF_ID", "정의 ID"), new("INSP_DEF_NAME", "정의명"), new("PROCESS_ID", "공정"),
                new("PRODUCT_ID", "품목"), new("INSPECTION_TYPE", "검사유형"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.InspectionDefList"));

        // 검사 항목(FACTORY_QCA_INSPECTION_ITEM) — 검사 항목 마스터(QMS.InspectionItemList).
        Register(new ScreenDefinition("FACTORY_QCA_INSPECTION_ITEM", "검사 항목",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ITEM_ID", "항목 ID"), new("ITEM_NAME", "항목명"), new("INSPECTION_TYPE", "검사유형"),
                new("MEASURE_TYPE", "측정유형"), new("UNIT", "단위"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.InspectionItemList"));

        // 수입검사 정보연결(FACTORY_QCA_IMPORT_INSPECTION_MAPPING) — 수입검사 방법 설정(QMS.IncomingInspMethodList).
        Register(new ScreenDefinition("FACTORY_QCA_IMPORT_INSPECTION_MAPPING", "수입검사 정보연결",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("METHOD_ID", "방법 ID"), new("METHOD_NAME", "방법명"), new("PRODUCT_ID", "품목"),
                new("SAMPLING_TYPE", "샘플링"), new("AQL_LEVEL", "AQL"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.IncomingInspMethodList"));

        // 공정/출하검사 정보연결(FACTORY_QCA_{PROCESS,SHIPMENT}_INSPECTION_MAPPING) — 검사 규격 카탈로그(QMS.InspectionSpecList).
        RegisterQcaSpecMapping("FACTORY_QCA_PROCESS_INSPECTION_MAPPING", "공정검사 정보연결");
        RegisterQcaSpecMapping("FACTORY_QCA_SHIPMENT_INSPECTION_MAPPING", "출하검사 정보연결");

        // ===== SmartUX FACTORY_PRC(구매) 점등 — 레거시 PRC_TB_PURCHASE_ORDER를 V052로 단순 포팅. 이동오더는 후속(IVT 이동 모델). =====
        // 구매오더 관리(FACTORY_PRC_PURCHASE_ORDER)·구매오더 현황(FACTORY_PRC_REPORT_PURCHASEORDER) — 발주 헤더(PRC.PurchaseOrderList).
        foreach (var (uiId, title) in new[] {
            ("FACTORY_PRC_PURCHASE_ORDER", "구매오더 관리"),
            ("FACTORY_PRC_REPORT_PURCHASEORDER", "구매오더 현황") })
            Register(new ScreenDefinition(uiId, title,
                Array.Empty<FieldDefinition>(),
                new GridColumnDefinition[]
                {
                    new("PURCHASE_ORDER_ID", "발주 ID"), new("PURCHASE_ORDER_NAME", "발주명"), new("PLANT_ID", "공장"),
                    new("VENDOR_ID", "거래처"), new("ORDER_DATE", "발주일"), new("INCOMING_DATE", "입고예정일"),
                    new("ORDER_QTY", "발주수량"), new("OWNER_ID", "담당자"), new("STATUS", "상태"), new("IS_HOLD", "홀드"),
                },
                QueryId: "PRC.PurchaseOrderList"));

        // ===== SmartUX FACTORY_SLS(판매) 점등 — 레거시 SLS_TB_SALES_ORDER/REQUEST를 V053으로 포팅. 납품현황은 SHP 재사용. =====
        // 판매 오더 관리(FACTORY_SLS_SALES_ORDER) — 판매오더 헤더(SLS.SalesOrderList).
        Register(new ScreenDefinition("FACTORY_SLS_SALES_ORDER", "판매 오더 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("SALES_ORDER_ID", "판매오더 ID"), new("SALES_ORDER_NAME", "판매오더명"), new("PLANT_ID", "공장"),
                new("CUSTOMER_ID", "고객"), new("PRODUCT_ID", "품목"), new("PLAN_START_DATE", "계획시작"),
                new("PLAN_END_DATE", "계획종료"), new("PLAN_QTY", "계획수량"), new("DELIVERED_QTY", "납품수량"),
                new("STATUS", "상태"), new("IS_HOLD", "홀드"),
            },
            QueryId: "SLS.SalesOrderList"));

        // 판매 요청(FACTORY_SLS_SALES_REQUEST) — 판매요청(SLS.SalesRequestList).
        Register(new ScreenDefinition("FACTORY_SLS_SALES_REQUEST", "판매 요청",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("SALES_REQUEST_ID", "요청 ID"), new("SALES_REQUEST_NAME", "요청명"), new("SALES_ORDER_ID", "판매오더"),
                new("CUSTOMER_ID", "고객"), new("PRODUCT_ID", "품목"), new("REQUEST_DATE", "요청일"),
                new("REQUEST_QTY", "요청수량"), new("STATUS", "상태"),
            },
            QueryId: "SLS.SalesRequestList"));

        // 납품 현황(FACTORY_SLS_REPORT_DELIVERY) — 출하 이력 재사용(SHP.ShipmentHistoryList).
        Register(new ScreenDefinition("FACTORY_SLS_REPORT_DELIVERY", "납품 현황",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("HISTORY_ID", "이력 ID"), new("DELIVERY_ORDER_ID", "출하지시"), new("SHIPPED_AT", "출하시각"),
                new("SHIPPED_QTY", "출하수량"), new("SHIPPED_BY", "출하자"), new("CARRIER", "운송사"), new("TRACKING_NO", "송장번호"),
            },
            QueryId: "SHP.ShipmentHistoryList"));

        // 출하 지시 관리(FACTORY_DLV_DELIVERY_ORDER) — 출하지시 마스터 조회(SHP.DeliveryOrderList).
        Register(new ScreenDefinition("FACTORY_DLV_DELIVERY_ORDER", "출하 지시 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ORDER_ID", "출하지시 ID"), new("CUSTOMER_NAME", "고객"), new("PLANT_ID", "공장 ID"),
                new("REQUESTED_DATE", "요청일"), new("SHIPPED_DATE", "출하일"), new("STATUS", "상태"), new("REMARK", "비고"),
            },
            QueryId: "SHP.DeliveryOrderList"));

        // 출하지시 현황(FACTORY_DLV_REPORT_DELIVERYORDER) — 동일 출하지시를 현황 중심으로 조회.
        Register(new ScreenDefinition("FACTORY_DLV_REPORT_DELIVERYORDER", "출하지시 현황",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ORDER_ID", "출하지시 ID"), new("CUSTOMER_NAME", "고객"), new("PLANT_ID", "공장 ID"),
                new("REQUESTED_DATE", "요청일"), new("SHIPPED_DATE", "출하일"), new("STATUS", "상태"),
            },
            QueryId: "SHP.DeliveryOrderList"));

        // 출하 처리(FACTORY_DLV_DELIVERY_RESULT) — 출하 이력 조회(SHP.ShipmentHistoryList).
        Register(new ScreenDefinition("FACTORY_DLV_DELIVERY_RESULT", "출하 처리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("HISTORY_ID", "이력 ID"), new("DELIVERY_ORDER_ID", "출하지시 ID"), new("SHIPPED_AT", "출하시각"),
                new("SHIPPED_QTY", "출하수량"), new("SHIPPED_BY", "출하자"), new("CARRIER", "운송사"), new("TRACKING_NO", "송장번호"),
            },
            QueryId: "SHP.ShipmentHistoryList"));

        // ===== SmartUX 추가 점등: QMS 부적합 현황(기존 백엔드) + EMS 점검항목 마스터(V036 신설). =====

        // 부적합 발생 현황(QMS_REP_NCR_STATUS) — 부적합/결함 발생 조회(기존 QMS.DefectList, NULL-guard 전체조회).
        Register(new ScreenDefinition("QMS_REP_NCR_STATUS", "부적합 발생 현황",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("DEFECT_ID", "부적합 ID"), new("LOT_ID", "LOT ID"), new("EQUIPMENT_ID", "설비 ID"),
                new("DEFECT_CLASS_ID", "결함분류"), new("DEFECT_COUNT", "결함수"), new("DEFECT_RATE", "결함률"),
                new("INSPECTED_AT", "검사시각"), new("INSPECTOR_ID", "검사자"), new("IS_CONFIRMED", "확정"),
            },
            QueryId: "QMS.DefectList"));

        // 설비 점검 항목 그룹 관리(FACTORY_EMS_STD_MAINT_ITEM_CLASS) — V036 신설 마스터(EMS.MaintItemClassList).
        Register(new ScreenDefinition("FACTORY_EMS_STD_MAINT_ITEM_CLASS", "설비 점검 항목 그룹 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ITEM_CLASS_ID", "항목 그룹 ID"), new("ITEM_CLASS_NAME", "항목 그룹명"), new("DESCRIPTION", "설명"),
            },
            QueryId: "EMS.MaintItemClassList"));

        // 설비 점검 항목 관리(FACTORY_EMS_STD_MAINT_ITEM) — V036 신설 마스터(EMS.MaintItemList).
        Register(new ScreenDefinition("FACTORY_EMS_STD_MAINT_ITEM", "설비 점검 항목 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ITEM_ID", "항목 ID"), new("ITEM_NAME", "항목명"), new("ITEM_CLASS_ID", "항목 그룹"),
                new("INSPECTION_METHOD", "점검방법"), new("UNIT", "단위"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "EMS.MaintItemList"));

        // 설비별 점검 항목 관리(FACTORY_EMS_STD_EQP_MAINT_ITEM) — V036 신설 매핑(EMS.EqpMaintItemList).
        Register(new ScreenDefinition("FACTORY_EMS_STD_EQP_MAINT_ITEM", "설비별 점검 항목 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQP_ITEM_ID", "매핑 ID"), new("EQUIPMENT_ID", "설비 ID"), new("ITEM_ID", "점검 항목 ID"),
                new("CYCLE_TYPE", "주기유형"), new("CYCLE_VALUE", "주기값"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "EMS.EqpMaintItemList"));

        // ===== SmartUX QMS 기준정보 마스터 점등(V037 신설) — 검사항목/검사정의/수입검사방법. 그리드 read는 인증만. =====

        // 검사항목 관리(QMS_STD_INSP_ITEM) — 검사항목 마스터(QMS.InspectionItemList).
        Register(new ScreenDefinition("QMS_STD_INSP_ITEM", "검사항목 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ITEM_ID", "검사항목 ID"), new("ITEM_NAME", "항목명"), new("INSPECTION_TYPE", "검사유형"),
                new("MEASURE_TYPE", "측정유형"), new("UNIT", "단위"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.InspectionItemList"));

        // 검사 관리(QMS_STD_INSP_DEF) — 검사 정의(공정/품목 단위) 마스터(QMS.InspectionDefList).
        Register(new ScreenDefinition("QMS_STD_INSP_DEF", "검사 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("INSP_DEF_ID", "검사정의 ID"), new("INSP_DEF_NAME", "검사명"), new("PROCESS_ID", "공정 ID"),
                new("PRODUCT_ID", "품목 ID"), new("INSPECTION_TYPE", "검사유형"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.InspectionDefList"));

        // 수입검사 방법 설정(QMS_STD_INSP_INCOMING_METHOD) — 품목별 수입검사 샘플링 방법(QMS.IncomingInspMethodList).
        Register(new ScreenDefinition("QMS_STD_INSP_INCOMING_METHOD", "수입검사 방법 설정",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("METHOD_ID", "방법 ID"), new("METHOD_NAME", "방법명"), new("PRODUCT_ID", "품목 ID"),
                new("SAMPLING_TYPE", "샘플링"), new("AQL_LEVEL", "AQL 수준"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.IncomingInspMethodList"));

        // ===== SmartUX QMS 계측기(Gauge) 관리 점등(V038 신설) — 계측기/검교정/RNR/수리. 그리드 read는 인증만. =====

        // 계측기 관리(QMS_GAUGE_MEASURE_EQUIPMENT_MGNT) — 계측기 마스터(QMS.GaugeList).
        Register(new ScreenDefinition("QMS_GAUGE_MEASURE_EQUIPMENT_MGNT", "계측기 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("GAUGE_ID", "계측기 ID"), new("GAUGE_NAME", "계측기명"), new("GAUGE_TYPE", "유형"),
                new("MODEL", "모델"), new("SERIAL_NO", "시리얼"), new("LOCATION", "위치"),
                new("NEXT_CALIBRATION_AT", "차기검교정"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.GaugeList"));

        // 검교정 계획 관리(QMS_GAUGE_CALIBRATION_PLAN) — 검교정 계획(QMS.GaugeCalibrationPlanList).
        Register(new ScreenDefinition("QMS_GAUGE_CALIBRATION_PLAN", "검교정 계획 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PLAN_ID", "계획 ID"), new("GAUGE_ID", "계측기 ID"), new("PLAN_NAME", "계획명"),
                new("SCHEDULED_DATE", "예정일"), new("CYCLE_TYPE", "주기"), new("ASSIGNEE_ID", "담당자"), new("STATUS", "상태"),
            },
            QueryId: "QMS.GaugeCalibrationPlanList"));

        // 검교정 내역 등록(QMS_GAUGE_CALIBRATION_RESULT) — 검교정 결과 이력(QMS.GaugeCalibrationResultList).
        Register(new ScreenDefinition("QMS_GAUGE_CALIBRATION_RESULT", "검교정 내역 등록",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("RESULT_ID", "내역 ID"), new("GAUGE_ID", "계측기 ID"), new("CALIBRATED_AT", "검교정일시"),
                new("CALIBRATED_BY", "수행자"), new("RESULT", "결과"), new("CERTIFICATE_NO", "성적서번호"), new("NEXT_DUE_AT", "차기예정"),
            },
            QueryId: "QMS.GaugeCalibrationResultList"));

        // RNR 계획 관리(QMS_GAUGE_RNR_PLAN) — Gage R&R 계획(QMS.GaugeRnrPlanList).
        Register(new ScreenDefinition("QMS_GAUGE_RNR_PLAN", "RNR 계획 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("RNR_PLAN_ID", "RNR 계획 ID"), new("GAUGE_ID", "계측기 ID"), new("PLAN_NAME", "계획명"),
                new("SCHEDULED_DATE", "예정일"), new("OPERATOR_COUNT", "측정자수"), new("TRIAL_COUNT", "반복수"),
                new("PART_COUNT", "시료수"), new("STATUS", "상태"),
            },
            QueryId: "QMS.GaugeRnrPlanList"));

        // RNR 평가 등록(QMS_GAUGE_RNR_RESULT) — Gage R&R 평가 결과(QMS.GaugeRnrResultList).
        Register(new ScreenDefinition("QMS_GAUGE_RNR_RESULT", "RNR 평가 등록",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("RNR_RESULT_ID", "평가 ID"), new("GAUGE_ID", "계측기 ID"), new("EVALUATED_AT", "평가일시"),
                new("EVALUATED_BY", "평가자"), new("GAGE_RR_PERCENT", "%GR&R"), new("NDC", "NDC"), new("JUDGEMENT", "판정"),
            },
            QueryId: "QMS.GaugeRnrResultList"));

        // 수리 내역 등록(QMS_GAUGE_REPAIR_RESULT) — 계측기 수리 이력(QMS.GaugeRepairResultList).
        Register(new ScreenDefinition("QMS_GAUGE_REPAIR_RESULT", "수리 내역 등록",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("REPAIR_ID", "수리 ID"), new("GAUGE_ID", "계측기 ID"), new("REPAIRED_AT", "수리일시"),
                new("REPAIRED_BY", "수리자"), new("FAILURE_DESC", "고장내용"), new("REPAIR_DESC", "수리내용"), new("COST", "비용"),
            },
            QueryId: "QMS.GaugeRepairResultList"));

        // ===== SmartUX QMS 협력사 관리(SPM) 점등(V039 신설) — 평가 항목/정의/연결/실적/시정조치. 그리드 read는 인증만. =====

        // 협력사 평가 항목(QMS_SPM_EVL_ITEM) — 평가 항목 마스터(QMS.SpmEvalItemList).
        Register(new ScreenDefinition("QMS_SPM_EVL_ITEM", "협력사 평가 항목",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ITEM_ID", "항목 ID"), new("ITEM_NAME", "항목명"), new("CATEGORY", "분류"),
                new("MAX_SCORE", "만점"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.SpmEvalItemList"));

        // 협력사 평가 정의(QMS_SPM_EVL_DEF) — 평가 양식/주기 마스터(QMS.SpmEvalDefList).
        Register(new ScreenDefinition("QMS_SPM_EVL_DEF", "협력사 평가 정의",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("DEF_ID", "정의 ID"), new("DEF_NAME", "정의명"), new("EVAL_CYCLE", "평가주기"),
                new("TARGET_TYPE", "대상유형"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.SpmEvalDefList"));

        // 협력사 평가 정보 연결(QMS_SPM_EVL_PARA) — 정의↔항목 가중치 연결(QMS.SpmEvalParamList).
        Register(new ScreenDefinition("QMS_SPM_EVL_PARA", "협력사 평가 정보 연결",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PARAM_ID", "연결 ID"), new("DEF_ID", "정의 ID"), new("ITEM_ID", "항목 ID"),
                new("WEIGHT", "가중치"), new("SORT_ORDER", "순서"),
            },
            QueryId: "QMS.SpmEvalParamList"));

        // 협력사 실적 관리(QMS_SPM_EVL_RESULT) — 협력사 평가 실적(QMS.SpmEvalResultList).
        Register(new ScreenDefinition("QMS_SPM_EVL_RESULT", "협력사 실적 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("RESULT_ID", "실적 ID"), new("SUPPLIER_ID", "협력사 ID"), new("SUPPLIER_NAME", "협력사명"),
                new("DEF_ID", "정의 ID"), new("EVAL_PERIOD", "평가기간"), new("TOTAL_SCORE", "총점"),
                new("GRADE", "등급"), new("EVALUATED_AT", "평가일시"),
            },
            QueryId: "QMS.SpmEvalResultList"));

        // 협력사 실적 조회(QMS_SPM_EVL_RESULT_VIEW) — 동일 실적을 조회 전용으로(QMS.SpmEvalResultList 재사용).
        Register(new ScreenDefinition("QMS_SPM_EVL_RESULT_VIEW", "협력사 실적 조회",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("RESULT_ID", "실적 ID"), new("SUPPLIER_ID", "협력사 ID"), new("SUPPLIER_NAME", "협력사명"),
                new("EVAL_PERIOD", "평가기간"), new("TOTAL_SCORE", "총점"), new("GRADE", "등급"), new("EVALUATED_AT", "평가일시"),
            },
            QueryId: "QMS.SpmEvalResultList"));

        // 시정 조치 결과 등록(QMS_SPM_ADMIN_ACTION_RESULT_REGIST) — 협력사 시정 조치 이력(QMS.SpmActionResultList).
        Register(new ScreenDefinition("QMS_SPM_ADMIN_ACTION_RESULT_REGIST", "시정 조치 결과 등록",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ACTION_ID", "조치 ID"), new("RESULT_ID", "실적 ID"), new("SUPPLIER_ID", "협력사 ID"),
                new("ACTION_DESC", "조치내용"), new("ACTION_DATE", "조치일"), new("STATUS", "상태"), new("COMPLETED_AT", "완료일"),
            },
            QueryId: "QMS.SpmActionResultList"));

        // ===== SmartUX QMS 검사(수입/공정/출하) 점등(V040 신설 QMS_INSPECTION) — 등록/이력/현황을 타입별 쿼리로 바인딩. =====
        var inspIncomingCols = new GridColumnDefinition[]
        {
            new("INSPECTION_ID", "검사 ID"), new("INSPECTION_TYPE", "유형"), new("LOT_ID", "LOT ID"),
            new("PRODUCT_ID", "품목 ID"), new("INSPECTED_AT", "검사일시"), new("RESULT", "결과"),
            new("SAMPLE_QTY", "표본수"), new("DEFECT_QTY", "불량수"),
        };
        Register(new ScreenDefinition("QMS_INSP_IMPORT_INSPECTION", "수입 검사 등록", Array.Empty<FieldDefinition>(), inspIncomingCols, QueryId: "QMS.IncomingInspectionList"));
        Register(new ScreenDefinition("QMS_INSP_IMPORT_REGIST_HIST", "수입 검사 이력 조회", Array.Empty<FieldDefinition>(), inspIncomingCols, QueryId: "QMS.IncomingInspectionList"));
        Register(new ScreenDefinition("QMS_REP_IMPORT_STATUS", "수입 검사 현황", Array.Empty<FieldDefinition>(), inspIncomingCols, QueryId: "QMS.IncomingInspectionList"));
        Register(new ScreenDefinition("QMS_INSP_PROCESS_INSPECTION", "공정 검사 등록", Array.Empty<FieldDefinition>(), inspIncomingCols, QueryId: "QMS.ProcessInspectionList"));
        Register(new ScreenDefinition("QMS_INSP_PROCESS_INSPECTION_LOT", "공정 검사 등록 (LOT)", Array.Empty<FieldDefinition>(), inspIncomingCols, QueryId: "QMS.ProcessInspectionList"));
        Register(new ScreenDefinition("QMS_INSP_PROCESS_REGIST_HIST", "공정 검사 이력 조회", Array.Empty<FieldDefinition>(), inspIncomingCols, QueryId: "QMS.ProcessInspectionList"));
        Register(new ScreenDefinition("QMS_REP_PROCESS_STATUS", "공정 검사 현황", Array.Empty<FieldDefinition>(), inspIncomingCols, QueryId: "QMS.ProcessInspectionList"));
        Register(new ScreenDefinition("QMS_INSP_SHIPPING_INSPECTION", "출하 검사 등록", Array.Empty<FieldDefinition>(), inspIncomingCols, QueryId: "QMS.ShippingInspectionList"));
        Register(new ScreenDefinition("QMS_INSP_SHIPPING_REGIST_HIST", "출하 검사 이력 조회", Array.Empty<FieldDefinition>(), inspIncomingCols, QueryId: "QMS.ShippingInspectionList"));
        Register(new ScreenDefinition("QMS_REP_SHIPPING_STATUS", "출하 검사 현황", Array.Empty<FieldDefinition>(), inspIncomingCols, QueryId: "QMS.ShippingInspectionList"));

        // ===== SmartUX QMS 장기재고검사(자재/제품) 점등(V041 신설 QMS_LONGTERM_INSPECTION) — 의뢰/결과/이력을 대상별 쿼리로. =====
        var ltInspCols = new GridColumnDefinition[]
        {
            new("LT_INSP_ID", "검사 ID"), new("TARGET_TYPE", "대상"), new("PRODUCT_ID", "품목 ID"), new("LOT_ID", "LOT ID"),
            new("WAREHOUSE", "창고"), new("REQUEST_DATE", "의뢰일"), new("INSPECTED_AT", "검사일시"),
            new("RESULT", "결과"), new("STATUS", "상태"),
        };
        Register(new ScreenDefinition("QMS_INSP_LONGTERM_REQUEST", "자재 장기재고 검사 의뢰 현황", Array.Empty<FieldDefinition>(), ltInspCols, QueryId: "QMS.MaterialLongtermInspectionList"));
        Register(new ScreenDefinition("QMS_LONGTERM_INSP_RESULT", "자재 장기재고 검사 결과 등록", Array.Empty<FieldDefinition>(), ltInspCols, QueryId: "QMS.MaterialLongtermInspectionList"));
        Register(new ScreenDefinition("QMS_INSP_LONGTERM_HISTORY", "자재 장기재고 검사 결과 이력", Array.Empty<FieldDefinition>(), ltInspCols, QueryId: "QMS.MaterialLongtermInspectionList"));
        Register(new ScreenDefinition("QMS_INSP_LONGTERM_PRODUCT_REQUEST", "제품 장기재고 검사 의뢰 현황", Array.Empty<FieldDefinition>(), ltInspCols, QueryId: "QMS.ProductLongtermInspectionList"));
        Register(new ScreenDefinition("QMS_INSP_LONGTERM_PRODUCT_INSP_RESULT", "제품 장기재고 검사 결과 등록", Array.Empty<FieldDefinition>(), ltInspCols, QueryId: "QMS.ProductLongtermInspectionList"));
        Register(new ScreenDefinition("QMS_INSP_LONGTERM_PRODUCT_INSP_HISTORY", "제품 장기재고 검사 결과 이력", Array.Empty<FieldDefinition>(), ltInspCols, QueryId: "QMS.ProductLongtermInspectionList"));

        // ===== SmartUX QMS 클레임(QMS_CLM) 점등(V042 신설 QMS_CLAIM). =====
        var claimCols = new GridColumnDefinition[]
        {
            new("CLAIM_ID", "클레임 ID"), new("CLAIM_NO", "클레임번호"), new("CUSTOMER_NAME", "고객사"),
            new("PRODUCT_ID", "품목 ID"), new("CLAIM_TYPE", "유형"), new("OCCURRED_DATE", "발생일"),
            new("SEVERITY", "심각도"), new("STATUS", "상태"),
        };
        Register(new ScreenDefinition("QMS_CLM_CLAIM_REGIST", "고객사 클레임 접수", Array.Empty<FieldDefinition>(), claimCols, QueryId: "QMS.ClaimList"));
        Register(new ScreenDefinition("QMS_CLM_CLAIM_RESULT", "클레임 처리 결과 등록", Array.Empty<FieldDefinition>(), claimCols, QueryId: "QMS.ClaimList"));
        Register(new ScreenDefinition("QMS_CLM_STATUS_VIEW", "클레임 현황 조회", Array.Empty<FieldDefinition>(), claimCols, QueryId: "QMS.ClaimList"));
        Register(new ScreenDefinition("QMS_CLM_RPT_OCCUR_STATUS", "클레임 발생 현황", Array.Empty<FieldDefinition>(), claimCols, QueryId: "QMS.ClaimList"));
        Register(new ScreenDefinition("QMS_CLM_REPORT_ACTION_STATUS", "클레임 처리 현황", Array.Empty<FieldDefinition>(), claimCols, QueryId: "QMS.ClaimList"));

        // ===== SmartUX QMS 품질보증(QCA) 점등(V043 신설) — NCR + Hold/Release. =====
        var ncrCols = new GridColumnDefinition[]
        {
            new("NCR_ID", "NCR ID"), new("NCR_NO", "NCR번호"), new("SOURCE_TYPE", "발생원"), new("LOT_ID", "LOT ID"),
            new("PRODUCT_ID", "품목 ID"), new("ISSUED_DATE", "발행일"), new("DISPOSITION", "처리"), new("STATUS", "상태"),
        };
        Register(new ScreenDefinition("QMS_QCA_NCR_ISSUE", "NCR 관리", Array.Empty<FieldDefinition>(), ncrCols, QueryId: "QMS.NcrList"));
        Register(new ScreenDefinition("QMS_QCA_NCR_OVERVIEW", "NCR 현황", Array.Empty<FieldDefinition>(), ncrCols, QueryId: "QMS.NcrList"));
        var holdCols = new GridColumnDefinition[]
        {
            new("HOLD_ID", "Hold ID"), new("LOT_ID", "LOT ID"), new("PRODUCT_ID", "품목 ID"), new("HOLD_TYPE", "유형"),
            new("RISK_RANGE", "Risk Range"), new("REQUESTED_BY", "요청자"), new("REQUESTED_AT", "요청일시"), new("STATUS", "상태"),
        };
        Register(new ScreenDefinition("QMS_QCA_RELEASE_HOLD_REG", "Hold/Release(Risk Range)", Array.Empty<FieldDefinition>(), holdCols, QueryId: "QMS.HoldReleaseList"));
        Register(new ScreenDefinition("QMS_QCA_PENDING_STATUS", "Hold/Release(Risk Range) 현황", Array.Empty<FieldDefinition>(), holdCols, QueryId: "QMS.HoldReleaseList"));

        // ===== SmartUX QMS 4M 변경 점등(V044 신설 QMS_4M_CHANGE). =====
        var fourMCols = new GridColumnDefinition[]
        {
            new("CHANGE_ID", "변경 ID"), new("CHANGE_NO", "변경번호"), new("CHANGE_TYPE", "4M 유형"),
            new("EQUIPMENT_ID", "설비 ID"), new("PRODUCT_ID", "품목 ID"), new("CHANGE_DATE", "변경일"), new("APPROVAL_STATUS", "승인상태"),
        };
        Register(new ScreenDefinition("QMS_4M_CHANGE_HISTORY", "4M 변경 이력 관리", Array.Empty<FieldDefinition>(), fourMCols, QueryId: "QMS.FourMChangeList"));
        Register(new ScreenDefinition("QMS_REP_CHANGE_STATUS", "변경점 발생 현황", Array.Empty<FieldDefinition>(), fourMCols, QueryId: "QMS.FourMChangeList"));

        // ===== SmartUX QMS 보고서성 잎 점등 — 신규 테이블 없이 기존 쿼리 재사용(계측기/협력사). =====
        var gaugeReportCols = new GridColumnDefinition[]
        {
            new("GAUGE_ID", "계측기 ID"), new("GAUGE_NAME", "계측기명"), new("GAUGE_TYPE", "유형"),
            new("LOCATION", "위치"), new("NEXT_CALIBRATION_AT", "차기검교정"), new("IS_ACTIVE", "활성"),
        };
        Register(new ScreenDefinition("QMS_MEASURE_INSTRUMENT_REPORT", "계측기 현황", Array.Empty<FieldDefinition>(), gaugeReportCols, QueryId: "QMS.GaugeList"));
        Register(new ScreenDefinition("QMS_MEQ_MEASURE_FAILURE_RATE", "계측기 측정 불량 현황", Array.Empty<FieldDefinition>(), gaugeReportCols, QueryId: "QMS.GaugeList"));
        Register(new ScreenDefinition("QMS_MEQ_CALIBRATION_STATUS", "계측기 검교정 현황", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("RESULT_ID", "내역 ID"), new("GAUGE_ID", "계측기 ID"), new("CALIBRATED_AT", "검교정일시"),
                new("RESULT", "결과"), new("CERTIFICATE_NO", "성적서번호"), new("NEXT_DUE_AT", "차기예정"),
            },
            QueryId: "QMS.GaugeCalibrationResultList"));
        Register(new ScreenDefinition("QMS_MEQ_MEASURE_REPAIR_DETAILS", "계측기 수리 현황", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("REPAIR_ID", "수리 ID"), new("GAUGE_ID", "계측기 ID"), new("REPAIRED_AT", "수리일시"),
                new("REPAIRED_BY", "수리자"), new("FAILURE_DESC", "고장내용"), new("REPAIR_DESC", "수리내용"),
            },
            QueryId: "QMS.GaugeRepairResultList"));
        var spmReportCols = new GridColumnDefinition[]
        {
            new("RESULT_ID", "실적 ID"), new("SUPPLIER_ID", "협력사 ID"), new("SUPPLIER_NAME", "협력사명"),
            new("EVAL_PERIOD", "평가기간"), new("TOTAL_SCORE", "총점"), new("GRADE", "등급"), new("EVALUATED_AT", "평가일시"),
        };
        Register(new ScreenDefinition("QMS_SPM_EVL_REPORT", "협력사 평가 현황", Array.Empty<FieldDefinition>(), spmReportCols, QueryId: "QMS.SpmEvalResultList"));
        Register(new ScreenDefinition("QMS_SPM_EVL_RESULT_COMPARISON", "협력사별 평가 결과 비교 조회", Array.Empty<FieldDefinition>(), spmReportCols, QueryId: "QMS.SpmEvalResultList"));

        // 검사 현황(QMS_REP_ITEM_STATUS.js) — 전체 검사 실행 조회(QMS.InspectionList, 타입 무관). uiId의 .js 접미사는 SmartUX 원본 그대로.
        Register(new ScreenDefinition("QMS_REP_ITEM_STATUS.js", "검사 현황",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("INSPECTION_ID", "검사 ID"), new("INSPECTION_TYPE", "유형"), new("LOT_ID", "LOT ID"),
                new("PRODUCT_ID", "품목 ID"), new("INSPECTED_AT", "검사일시"), new("RESULT", "결과"), new("IS_CONFIRMED", "확정"),
            },
            QueryId: "QMS.InspectionList"));

        // ===== SmartUX EMS 예비품 그룹/입출고 점등(V045 신설) + 잔여 BM/PM 오더(기존 EMS 쿼리 재사용). =====
        Register(new ScreenDefinition("FACTORY_EMS_STD_SPARE_PART_CLASS", "Spare Part 그룹 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("PART_CLASS_ID", "그룹 ID"), new("PART_CLASS_NAME", "그룹명"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성") },
            QueryId: "EMS.SparePartClassList"));
        var spareInoutCols = new GridColumnDefinition[]
        {
            new("INOUT_ID", "입출고 ID"), new("PART_ID", "부품 ID"), new("TRANSACTION_TYPE", "유형"), new("QUANTITY", "수량"),
            new("FROM_LOCATION", "출발위치"), new("TO_LOCATION", "도착위치"), new("TRANSACTION_AT", "처리일시"), new("PROCESSED_BY", "처리자"),
        };
        Register(new ScreenDefinition("FACTORY_EMS_STD_SPARE_PART_INCOMING", "Spare Part 입고", Array.Empty<FieldDefinition>(), spareInoutCols, QueryId: "EMS.SparePartIncomingList"));
        Register(new ScreenDefinition("FACTORY_EMS_STD_SPARE_PART_MOVE", "Spare Part 이동", Array.Empty<FieldDefinition>(), spareInoutCols, QueryId: "EMS.SparePartMoveList"));
        Register(new ScreenDefinition("FACTORY_EMS_STD_SPARE_PART_MOVE_GRIDTYPE", "Spare Part 이동 그리드", Array.Empty<FieldDefinition>(), spareInoutCols, QueryId: "EMS.SparePartMoveList"));
        Register(new ScreenDefinition("FACTORY_EMS_STD_SPARE_PART_SCRAP", "Spare Part 폐기", Array.Empty<FieldDefinition>(), spareInoutCols, QueryId: "EMS.SparePartScrapList"));
        Register(new ScreenDefinition("FACTORY_EMS_STD_SPARE_PART_SCRAP_GRIDTYPE", "Spare Part 폐기 그리드", Array.Empty<FieldDefinition>(), spareInoutCols, QueryId: "EMS.SparePartScrapList"));
        Register(new ScreenDefinition("FACTORY_EMS_STD_SPARE_PART_INOUT_HISTORY", "Spare Part 입출고 이력", Array.Empty<FieldDefinition>(), spareInoutCols, QueryId: "EMS.SparePartInoutList"));

        // 잔여 BM(작업지시) 화면 — 기존 EMS.WorkOrderList 재사용.
        var bmOrderCols = new GridColumnDefinition[]
        {
            new("WO_ID", "작업지시 ID"), new("EQUIPMENT_ID", "설비 ID"), new("WO_TYPE", "유형"), new("DESCRIPTION", "설명"),
            new("ASSIGNEE_ID", "담당자"), new("ISSUED_AT", "발행일시"), new("STATUS", "상태"),
        };
        Register(new ScreenDefinition("FACTORY_EMS_BM_ORDER_REQUEST", "설비 수리 요청", Array.Empty<FieldDefinition>(), bmOrderCols, QueryId: "EMS.WorkOrderList"));
        Register(new ScreenDefinition("FACTORY_EMS_BM_ORDER_REPAIR", "설비 수리 등록", Array.Empty<FieldDefinition>(), bmOrderCols, QueryId: "EMS.WorkOrderList"));
        Register(new ScreenDefinition("FACTORY_EMS_BM_ORDER_REPAIR_REGISTER_GRIDTYPE", "설비 수리 등록 그리드", Array.Empty<FieldDefinition>(), bmOrderCols, QueryId: "EMS.WorkOrderList"));
        Register(new ScreenDefinition("FACTORY_EMS_BM_ORDER_RESULT_GRIDTYPE", "설비 보전 결과 그리드", Array.Empty<FieldDefinition>(), bmOrderCols, QueryId: "EMS.WorkOrderList"));

        // 잔여 PM(보전계획 결과) 화면 — 기존 EMS.MaintenancePlanList 재사용.
        var pmPlanCols = new GridColumnDefinition[]
        {
            new("PLAN_ID", "계획 ID"), new("PLAN_NAME", "계획명"), new("EQUIPMENT_ID", "설비 ID"), new("PLAN_TYPE", "유형"),
            new("CYCLE_TYPE", "주기"), new("SCHEDULED_DATE", "예정일"), new("STATUS", "상태"),
        };
        Register(new ScreenDefinition("FACTORY_EMS_PM_ORDER_RESULT_RESULT", "PM 결과 등록", Array.Empty<FieldDefinition>(), pmPlanCols, QueryId: "EMS.MaintenancePlanList"));
        Register(new ScreenDefinition("FACTORY_EMS_PM_ORDER_RESULT_LIST", "PM 결과 조회", Array.Empty<FieldDefinition>(), pmPlanCols, QueryId: "EMS.MaintenancePlanList"));

        // ===== SmartUX 기준정보(FACTORY_STD) — SmartUX 'SINGLE' 기준정보 메뉴는 기존 MDM 마스터의 별칭 경로다. 신규 백엔드 없이 기존 MDM 쿼리 재사용. =====
        var stdPlantCols = new GridColumnDefinition[] { new("PLANT_ID", "공장 ID"), new("PLANT_NAME", "공장명"), new("DESCRIPTION", "설명"), new("COUNTRY", "국가"), new("TIME_ZONE", "표준시") };
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_PLANT", "공장 관리", Array.Empty<FieldDefinition>(), stdPlantCols, QueryId: "MDM.PlantList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_AREA", "Area 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("AREA_ID", "AREA ID"), new("AREA_NAME", "AREA명"), new("DESCRIPTION", "설명"), new("PLANT_ID", "공장 ID") }, QueryId: "MDM.AreaList"));
        var stdEquipCols = new GridColumnDefinition[] { new("EQUIPMENT_ID", "설비 ID"), new("EQUIPMENT_NAME", "설비명"), new("PLANT_ID", "공장 ID"), new("AREA_ID", "구역 ID"), new("EQUIPMENT_TYPE", "설비유형"), new("EQUIPMENT_CLASS_ID", "설비 그룹"), new("VALID_STATE", "상태") };
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_EQUIPMENT_DEF", "설비 관리", Array.Empty<FieldDefinition>(), stdEquipCols, QueryId: "MDM.EquipmentList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_EQUIPMENT", "설비", Array.Empty<FieldDefinition>(), stdEquipCols, QueryId: "MDM.EquipmentList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_EQUIPMENTCLASS", "설비 그룹 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("EQUIPMENT_CLASS_ID", "설비 그룹 ID"), new("EQUIPMENT_CLASS_NAME", "설비 그룹명"), new("DESCRIPTION", "설명") }, QueryId: "MDM.EquipmentClassList"));
        var stdProductCols = new GridColumnDefinition[] { new("PRODUCT_ID", "품목 ID"), new("PRODUCT_NAME", "품목명"), new("DESCRIPTION", "설명"), new("PRODUCT_TYPE", "유형"), new("UNIT", "단위"), new("VALID_STATE", "상태") };
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_ITEM_DEF", "품목 관리", Array.Empty<FieldDefinition>(), stdProductCols, QueryId: "MDM.ProductList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_ITEM", "품목", Array.Empty<FieldDefinition>(), stdProductCols, QueryId: "MDM.ProductList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_PRODUCT_SPEC", "제품 사양 관리", Array.Empty<FieldDefinition>(), stdProductCols, QueryId: "MDM.ProductList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_ITEMCLASS", "품목 그룹 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("ITEM_CLASS_ID", "품목 그룹 ID"), new("ITEM_CLASS_NAME", "품목 그룹명"), new("DESCRIPTION", "설명") }, QueryId: "MDM.ItemClassList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_PROCESS", "프로세스 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("PROCESS_ID", "프로세스 ID"), new("PROCESS_NAME", "프로세스명"), new("PROCESS_CLASS_ID", "프로세스 그룹"), new("DESCRIPTION", "설명") }, QueryId: "MDM.ProcessList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_PROCESSCLASS", "프로세스 그룹 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("PROCESS_CLASS_ID", "프로세스 그룹 ID"), new("PROCESS_CLASS_NAME", "프로세스 그룹명"), new("DESCRIPTION", "설명") }, QueryId: "MDM.ProcessClassList"));
        var stdSegmentCols = new GridColumnDefinition[] { new("SEGMENT_ID", "공정 ID"), new("SEGMENT_NAME", "공정명"), new("SEGMENT_CLASS_ID", "공정 그룹"), new("DESCRIPTION", "설명") };
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_SEGMENT", "공정", Array.Empty<FieldDefinition>(), stdSegmentCols, QueryId: "MDM.SegmentList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_SEGMENT_DEF", "공정 관리", Array.Empty<FieldDefinition>(), stdSegmentCols, QueryId: "MDM.SegmentList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_SEGMENTCLASS", "공정 그룹 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("SEGMENT_CLASS_ID", "공정 그룹 ID"), new("SEGMENT_CLASS_NAME", "공정 그룹명"), new("DESCRIPTION", "설명") }, QueryId: "MDM.SegmentClassList"));
        var stdCodeCols = new GridColumnDefinition[] { new("CODE_ID", "코드 ID"), new("CODE_NAME", "코드명"), new("CODE_CLASS_ID", "코드 그룹"), new("SORT_ORDER", "정렬"), new("VALID_STATE", "상태") };
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_REASONCODE", "사유코드 관리", Array.Empty<FieldDefinition>(), stdCodeCols, QueryId: "MDM.CodeList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_CODE", "코드", Array.Empty<FieldDefinition>(), stdCodeCols, QueryId: "MDM.CodeList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_REASONCODECLASS", "사유코드 그룹 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("CODE_CLASS_ID", "코드 그룹 ID"), new("CODE_CLASS_NAME", "코드 그룹명"), new("DESCRIPTION", "설명") }, QueryId: "MDM.CodeClassList"));
        var stdRoutingCols = new GridColumnDefinition[] { new("ROUTING_ID", "라우팅 ID"), new("ROUTING_NAME", "라우팅명"), new("PRODUCT_ID", "품목 ID"), new("DESCRIPTION", "설명") };
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_PROCESSPATH", "라우팅 관리", Array.Empty<FieldDefinition>(), stdRoutingCols, QueryId: "MDM.RoutingList"));
        Register(new ScreenDefinition("FACTORY_STD_WO_PROCESS_PATH", "W/O 라우팅 관리", Array.Empty<FieldDefinition>(), stdRoutingCols, QueryId: "MDM.RoutingList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_BILL_OF_MATERIAL", "BOM 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("BOM_ID", "BOM ID"), new("PRODUCT_ID", "제품 ID"), new("COMPONENT_ID", "부품 ID"), new("QUANTITY", "수량") }, QueryId: "MDM.BomList"));

        // ===== SmartUX 시스템관리(SYSTEM_2) — 사용자/권한/메뉴/UIID/코드는 기존 SYS·MDM 쿼리 재사용(신규 백엔드 0). =====
        Register(new ScreenDefinition("SYSTEM_2_USER_MANAGEMENT", "사용자 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("USER_ID", "사용자 ID"), new("USER_NAME", "사용자명"), new("EMAIL", "이메일"), new("ROLE_ID", "역할"), new("LANGUAGE", "언어"), new("IS_ACTIVE", "활성"), new("LAST_LOGIN_AT", "최근로그인") }, QueryId: "SYS.ListUsers"));
        var roleCols = new GridColumnDefinition[] { new("ROLE_ID", "역할 ID"), new("ROLE_NAME", "역할명"), new("DESCRIPTION", "설명"), new("PERMISSIONS", "권한") };
        Register(new ScreenDefinition("SYSTEM_2_AUTH_MANAGEMENT", "권한 관리", Array.Empty<FieldDefinition>(), roleCols, QueryId: "SYS.ListRoles"));
        Register(new ScreenDefinition("SYSTEM_2_AUTH_MANAGEMENT_NEW", "권한 그룹 관리", Array.Empty<FieldDefinition>(), roleCols, QueryId: "SYS.ListRoles"));
        Register(new ScreenDefinition("SYSTEM_2_MENU_AUTH_MANAGEMENT", "메뉴별 권한 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("MENU_ID", "메뉴 ID"), new("MENU_NAME", "메뉴명"), new("PARENT_MENU_ID", "상위"), new("MENU_TYPE", "유형"), new("UI_ID", "화면") }, QueryId: "SYS.ListMenus"));
        Register(new ScreenDefinition("SYSTEM_2_UIID_MANAGEMENT", "UIID 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("UI_ID", "UI ID"), new("TITLE", "제목") }, QueryId: "SYS.ListScreenDefinitions"));
        Register(new ScreenDefinition("SYSTEM_2_CODE_MANAGEMENT", "코드 관리", Array.Empty<FieldDefinition>(), stdCodeCols, QueryId: "MDM.CodeList"));

        // ===== SmartUX 기준정보(FACTORY_STD) 신규 마스터 점등(V046 신설) — 작업자/작업조/달력/거래처/납품처. =====
        Register(new ScreenDefinition("FACTORY_STD_WORKER_CLASS", "작업자 그룹 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("WORKER_CLASS_ID", "그룹 ID"), new("WORKER_CLASS_NAME", "그룹명"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성") }, QueryId: "MDM.WorkerClassList"));
        var workerCols = new GridColumnDefinition[] { new("WORKER_ID", "작업자 ID"), new("WORKER_NAME", "작업자명"), new("WORKER_CLASS_ID", "그룹"), new("EMPLOYEE_NO", "사번"), new("DEPARTMENT", "부서"), new("PLANT_ID", "공장 ID"), new("IS_ACTIVE", "활성") };
        Register(new ScreenDefinition("FACTORY_STD_WORKER_DEF", "작업자 관리", Array.Empty<FieldDefinition>(), workerCols, QueryId: "MDM.WorkerList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_WORKER", "작업자", Array.Empty<FieldDefinition>(), workerCols, QueryId: "MDM.WorkerList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_SHIFT", "작업조 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("SHIFT_ID", "작업조 ID"), new("SHIFT_NAME", "작업조명"), new("START_TIME", "시작"), new("END_TIME", "종료"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성") }, QueryId: "MDM.ShiftList"));
        Register(new ScreenDefinition("FACTORY_STD_WORK_CALENDAR", "Work Calendar 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("CALENDAR_ID", "달력 ID"), new("CALENDAR_DATE", "일자"), new("DAY_TYPE", "구분"), new("SHIFT_ID", "작업조"), new("PLANT_ID", "공장 ID"), new("DESCRIPTION", "설명") }, QueryId: "MDM.WorkCalendarList"));
        var customerCols = new GridColumnDefinition[] { new("CUSTOMER_ID", "거래처 ID"), new("CUSTOMER_NAME", "거래처명"), new("CUSTOMER_TYPE", "유형"), new("CONTACT_NAME", "담당자"), new("PHONE", "연락처"), new("ADDRESS", "주소"), new("IS_ACTIVE", "활성") };
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_CUSTOMER", "거래처 관리", Array.Empty<FieldDefinition>(), customerCols, QueryId: "MDM.CustomerList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_DELIVERY", "납품처 관리", Array.Empty<FieldDefinition>(), customerCols, QueryId: "MDM.CustomerList"));
        Register(new ScreenDefinition("FACTORY_STD_SINGLE_DELIVERY_ITEM", "납품처 품목 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("DELIVERY_ITEM_ID", "납품품목 ID"), new("CUSTOMER_ID", "거래처 ID"), new("PRODUCT_ID", "품목 ID"), new("DELIVERY_CODE", "납품코드"), new("UNIT_PRICE", "단가"), new("IS_ACTIVE", "활성") }, QueryId: "MDM.DeliveryItemList"));

        // ===== SmartUX 시스템관리(SYSTEM_2) 신규 마스터 점등(V047 신설) — 공지/메시지/다국어/Rule. =====
        Register(new ScreenDefinition("SYSTEM_2_NOTICE_MANAGEMENT", "공지사항 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("NOTICE_ID", "공지 ID"), new("TITLE", "제목"), new("NOTICE_TYPE", "유형"), new("POSTED_BY", "게시자"), new("POSTED_AT", "게시일시"), new("IS_ACTIVE", "활성") }, QueryId: "SYS.NoticeList"));
        Register(new ScreenDefinition("SYSTEM_2_MESSAGE_CLASS_MANAGEMENT", "메세지 그룹 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("MSG_CLASS_ID", "그룹 ID"), new("MSG_CLASS_NAME", "그룹명"), new("DESCRIPTION", "설명") }, QueryId: "SYS.MessageClassList"));
        Register(new ScreenDefinition("SYSTEM_2_MESSAGE_MANAGEMENT", "메세지 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("MSG_ID", "메시지 ID"), new("MSG_CLASS_ID", "그룹"), new("MSG_CODE", "코드"), new("MSG_TEXT", "내용"), new("LANGUAGE", "언어") }, QueryId: "SYS.MessageList"));
        Register(new ScreenDefinition("SYSTEM_2_LANGUAGE_CLASS_MANAGEMENT", "다국어 그룹 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("LANG_CLASS_ID", "그룹 ID"), new("LANG_CLASS_NAME", "그룹명"), new("DESCRIPTION", "설명") }, QueryId: "SYS.LanguageClassList"));
        Register(new ScreenDefinition("SYSTEM_2_LANGUAGE_MANAGEMENT", "다국어 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("I18N_ID", "항목 ID"), new("LANG_CLASS_ID", "그룹"), new("MESSAGE_KEY", "키"), new("LANGUAGE", "언어"), new("TRANSLATION", "번역") }, QueryId: "SYS.I18nList"));
        Register(new ScreenDefinition("SYSTEM_2_RULE_MANAGEMENT", "Rule 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("RULE_ID", "Rule ID"), new("RULE_NAME", "Rule명"), new("RULE_TYPE", "유형"), new("IS_ACTIVE", "활성"), new("DESCRIPTION", "설명") }, QueryId: "SYS.RuleList"));

        // ===== SmartUX 자재/재고(FACTORY_IVT) 점등(V048 신설) — 자재 LOT/재고 + 입출고 트랜잭션(입고/이동/불출). =====
        var ivtLotCols = new GridColumnDefinition[]
        {
            new("LOT_ID", "LOT ID"), new("MATERIAL_ID", "자재 ID"), new("LOT_NO", "LOT 번호"), new("WAREHOUSE", "창고"),
            new("CURRENT_QTY", "현재고"), new("UNIT", "단위"), new("STATUS", "상태"), new("RECEIVED_AT", "입고일시"),
        };
        Register(new ScreenDefinition("FACTORY_IVT_MATERIAL_LOT_MANAGEMENT", "자재 LOT 관리", Array.Empty<FieldDefinition>(), ivtLotCols, QueryId: "IVT.MaterialLotList"));
        Register(new ScreenDefinition("FACTORY_IVT_CONSUMABLE_LOT", "자재", Array.Empty<FieldDefinition>(), ivtLotCols, QueryId: "IVT.MaterialLotList"));
        Register(new ScreenDefinition("FACTORY_IVT_INVENTORY_STATUS", "재고 관리", Array.Empty<FieldDefinition>(), ivtLotCols, QueryId: "IVT.MaterialLotList"));
        var ivtTxCols = new GridColumnDefinition[]
        {
            new("TX_ID", "트랜잭션 ID"), new("LOT_ID", "LOT ID"), new("MATERIAL_ID", "자재 ID"), new("TX_TYPE", "유형"),
            new("QTY", "수량"), new("FROM_WAREHOUSE", "출발창고"), new("TO_WAREHOUSE", "도착창고"), new("TX_AT", "처리일시"),
            new("PROCESSED_BY", "처리자"), new("STATUS", "상태"),
        };
        Register(new ScreenDefinition("FACTORY_IVT_MATERIAL_INCOMING_MANAGEMENT", "입고 관리", Array.Empty<FieldDefinition>(), ivtTxCols, QueryId: "IVT.IncomingList"));
        Register(new ScreenDefinition("FACTORY_IVT_MOVE_ODER", "자재 이동", Array.Empty<FieldDefinition>(), ivtTxCols, QueryId: "IVT.MoveList"));
        Register(new ScreenDefinition("FACTORY_IVT_MATERIAL_DISPENSING", "자재 불출 처리", Array.Empty<FieldDefinition>(), ivtTxCols, QueryId: "IVT.DispensingList"));
        Register(new ScreenDefinition("FACTORY_IVT_MATERIAL_DISPENSING_REQUEST", "자재 불출 요청", Array.Empty<FieldDefinition>(), ivtTxCols, QueryId: "IVT.DispensingList"));

        // ===== SmartUX 공통(FACTORY_COM) — 코드/통화는 기존 MDM 재사용, 알람/상태/라벨/ID채번은 V049 신규 마스터. =====
        var comCodeClassCols = new GridColumnDefinition[] { new("CODE_CLASS_ID", "코드 그룹 ID"), new("CODE_CLASS_NAME", "코드 그룹명"), new("DESCRIPTION", "설명") };
        Register(new ScreenDefinition("FACTORY_COM_CODE_CLASS", "코드 그룹 관리", Array.Empty<FieldDefinition>(), comCodeClassCols, QueryId: "MDM.CodeClassList"));
        Register(new ScreenDefinition("FACTORY_COM_CODE_CODE", "코드 관리", Array.Empty<FieldDefinition>(), stdCodeCols, QueryId: "MDM.CodeList"));
        Register(new ScreenDefinition("FACTORY_COM_CURRENCY_CODE", "통화 코드 관리", Array.Empty<FieldDefinition>(), stdCodeCols, QueryId: "MDM.CodeList"));
        Register(new ScreenDefinition("FACTORY_COM_ALARM_CLASS", "알람 그룹 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("ALARM_CLASS_ID", "그룹 ID"), new("ALARM_CLASS_NAME", "그룹명"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성") }, QueryId: "COM.AlarmClassList"));
        Register(new ScreenDefinition("FACTORY_COM_ALARM_DEF", "알람 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("ALARM_ID", "알람 ID"), new("ALARM_NAME", "알람명"), new("ALARM_CLASS_ID", "그룹"), new("SEVERITY", "심각도"), new("MESSAGE", "메시지"), new("IS_ACTIVE", "활성") }, QueryId: "COM.AlarmList"));
        Register(new ScreenDefinition("FACTORY_COM_CODE_STATE_MODEL", "상태 모델 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("MODEL_ID", "모델 ID"), new("MODEL_NAME", "모델명"), new("TARGET_ENTITY", "대상"), new("DESCRIPTION", "설명") }, QueryId: "COM.StateModelList"));
        var comStateCols = new GridColumnDefinition[] { new("STATE_ID", "상태 ID"), new("MODEL_ID", "모델"), new("STATE_NAME", "상태명"), new("STATE_CODE", "코드"), new("SORT_ORDER", "순서"), new("IS_INITIAL", "초기") };
        Register(new ScreenDefinition("FACTORY_COM_CODE_STATE", "상태 코드 관리", Array.Empty<FieldDefinition>(), comStateCols, QueryId: "COM.StateList"));
        Register(new ScreenDefinition("FACTORY_COM_CODE_STATE_TRANSITION", "상태 관리", Array.Empty<FieldDefinition>(), comStateCols, QueryId: "COM.StateList"));
        Register(new ScreenDefinition("FACTORY_COM_LABEL", "라벨 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("LABEL_ID", "라벨 ID"), new("LABEL_NAME", "라벨명"), new("LABEL_TYPE", "유형"), new("IS_ACTIVE", "활성") }, QueryId: "COM.LabelList"));
        Register(new ScreenDefinition("FACTORY_COM_CODE_ID_DEFINITION", "ID 채번 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("RULE_ID", "규칙 ID"), new("RULE_NAME", "규칙명"), new("PREFIX", "접두"), new("SEQ_LENGTH", "자릿수"), new("CURRENT_SEQ", "현재값"), new("RESET_CYCLE", "리셋주기"), new("DESCRIPTION", "설명") }, QueryId: "COM.IdRuleList"));
    }

    public void Register(ScreenDefinition definition) => _defs[definition.UiId] = definition;

    // FACTORY_QCA 검사 실행(수입/공정/출하) 공용 그리드 — QMS_INSPECTION 컬럼 동일(제목/쿼리만 상이).
    private void RegisterQcaInspection(string uiId, string title, string queryId)
        => Register(new ScreenDefinition(uiId, title,
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("INSPECTION_ID", "검사 ID"), new("LOT_ID", "LOT"), new("PRODUCT_ID", "품목"), new("EQUIPMENT_ID", "설비"),
                new("SPEC_ID", "규격"), new("INSPECTED_AT", "검사시각"), new("INSPECTOR_ID", "검사자"),
                new("RESULT", "결과"), new("SAMPLE_QTY", "샘플수"), new("DEFECT_QTY", "불량수"), new("IS_CONFIRMED", "확정"),
            },
            QueryId: queryId));

    // FACTORY_QCA 공정/출하 정보연결 — 검사 규격 카탈로그(QMS_INSPECTION_SPEC).
    private void RegisterQcaSpecMapping(string uiId, string title)
        => Register(new ScreenDefinition(uiId, title,
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("SPEC_ID", "규격 ID"), new("SPEC_NAME", "규격명"), new("PROCESS_ID", "공정"), new("ITEM_NAME", "항목"),
                new("MEASURE_TYPE", "측정유형"), new("NOMINAL_VALUE", "기준값"), new("TOLERANCE_PLUS", "상한공차"),
                new("TOLERANCE_MINUS", "하한공차"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.InspectionSpecList"));

    public bool TryGet(string uiId, out ScreenDefinition? definition)
        => _defs.TryGetValue(uiId ?? string.Empty, out definition);

    public ScreenDefinition? Get(string uiId)
        => _defs.TryGetValue(uiId ?? string.Empty, out var d) ? d : null;

    public Task<ScreenDefinition?> GetAsync(string uiId, CancellationToken ct = default)
        => Task.FromResult(Get(uiId));
}

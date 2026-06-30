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
    }

    public void Register(ScreenDefinition definition) => _defs[definition.UiId] = definition;

    public bool TryGet(string uiId, out ScreenDefinition? definition)
        => _defs.TryGetValue(uiId ?? string.Empty, out definition);

    public ScreenDefinition? Get(string uiId)
        => _defs.TryGetValue(uiId ?? string.Empty, out var d) ? d : null;

    public Task<ScreenDefinition?> GetAsync(string uiId, CancellationToken ct = default)
        => Task.FromResult(Get(uiId));
}

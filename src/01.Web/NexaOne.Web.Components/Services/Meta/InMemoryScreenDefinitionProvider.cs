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
    }

    public void Register(ScreenDefinition definition) => _defs[definition.UiId] = definition;

    public bool TryGet(string uiId, out ScreenDefinition? definition)
        => _defs.TryGetValue(uiId ?? string.Empty, out definition);

    public ScreenDefinition? Get(string uiId)
        => _defs.TryGetValue(uiId ?? string.Empty, out var d) ? d : null;

    public Task<ScreenDefinition?> GetAsync(string uiId, CancellationToken ct = default)
        => Task.FromResult(Get(uiId));
}

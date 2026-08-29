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
            QueryId: "MDM.PlantList", Purpose: ScreenPurpose.Inquiry));

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
            SaveQueryId: "MDM.CreatePlant", DeleteQueryId: "MDM.DeletePlant",
            Purpose: ScreenPurpose.Register));

        // 데모 시드: 레이아웃(WYSIWYG) 화면 — 좌측 공장 그리드(MDM.PlantList) + 우측 등록 폼/저장 버튼(MDM.CreatePlant)을
        // 한 화면에 조합한다. /meta/DEMO_LAYOUT 이 LayoutRenderer로 렌더되는 레이아웃 런타임 end-to-end 시연.
        Register(new ScreenDefinition("DEMO_LAYOUT", "데모 — 레이아웃(그리드+폼)",
            Array.Empty<FieldDefinition>(),
            DeleteQueryId: "MDM.DeletePlant",
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
            },
            Purpose: ScreenPurpose.Manage));

        // 대시보드(요약) — 모듈 횡단 카운트(SYS.DashboardSummary 1행)를 KPI 카드 5장으로 표시(Phase-2 KPI 위젯 1호).
        // 단일 쿼리를 5개 위젯이 공유(런타임이 distinct 쿼리 1회만 실행), 컬럼만 다르게 바인딩한다.
        Register(new ScreenDefinition("DASHBOARD_SUMMARY", "대시보드 — 운영 요약",
            Array.Empty<FieldDefinition>(),
            RefreshIntervalSeconds: 30,
            // 컨테이너(Span=12) 아래 두 섹션 스택 — 상단 KPI 밴드 + 하단 최근 활동 그리드(빈 공간 구조화, 디자인 v2 2차).
            Layout: new ColumnNode
            {
                Id = "dash-stack", Span = 12,
                Children = new LayoutNode[]
                {
                    new SectionNode
                    {
                        Id = "dash-sec", Title = "운영 요약(30초 자동 새로고침)",
                        Children = new LayoutNode[]
                        {
                            new RowNode { Id = "dash-row", Children = new LayoutNode[]
                            {
                                // LinkUiId(P3-12) — 카드 클릭=관련 화면 드릴다운(대상 화면이 자명한 카드만).
                                DashKpi("dash-alarm", "활성 알람", "ACTIVE_ALARMS", "EES_POPUP_MONITORING_DASHBOARD"),
                                DashKpi("dash-wo", "진행 작업지시", "OPEN_WORK_ORDERS"),
                                DashKpi("dash-plan", "가동 생산계획", "ACTIVE_PLANS"),
                                DashKpi("dash-recipe", "레시피 승인 대기", "PENDING_RECIPE_APPROVALS"),
                                DashKpi("dash-ship", "출하 대기", "OPEN_DELIVERY_ORDERS"),
                            } },
                        },
                    },
                    // 시간대별 로그 추이(TrendChartWidget=RadzenChart) — SYS.AppLogHourlyTrend(시간 버킷별 건수).
                    // 자체 RowNode에 단독 배치해 풀폭으로 그로우(.layout-chart), 섹션 카드와 톤 일관.
                    new RowNode
                    {
                        Id = "dash-trend-row",
                        Children = new LayoutNode[]
                        {
                            new TrendChartWidget
                            {
                                Id = "dash-trend", Label = "시간대별 로그 추이",
                                QueryId = "SYS.AppLogHourlyTrend", TimeColumn = "HOUR_LABEL", ValueColumn = "LOG_COUNT",
                            },
                        },
                    },
                    // 최근 시스템 로그(SYS.AppLogList 재사용 — Warning+ 최근순). 운영자가 대시보드에서 이상 신호를 바로 본다.
                    new SectionNode
                    {
                        Id = "dash-log-sec", Title = "최근 시스템 로그",
                        Children = new LayoutNode[]
                        {
                            new GridWidget
                            {
                                Id = "dash-log", QueryId = "SYS.AppLogList",
                                Columns = new GridColumnDefinition[]
                                {
                                    new("LOGGED_AT", "발생시각"), new("LOG_LEVEL", "레벨"),
                                    new("CATEGORY", "카테고리"), new("MESSAGE", "메시지"),
                                },
                            },
                        },
                    },
                },
            },
            Purpose: ScreenPurpose.Report));

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
            QueryId: "QMS.DefectClassList", Purpose: ScreenPurpose.Inquiry));

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

        // 시스템관리 — 배치 작업 관리(SYSTEM_2_BATCH_PROC_MANAGEMENT, V066). SYS_MENU_MGMT 구조 미러.
        // 1차 범위 = 정의 CRUD까지 — 실행 엔진(BATCH_RULE 스케줄 실행)은 후속 슬라이스(마이그레이션 주석 참조).
        Register(new ScreenDefinition("SYSTEM_2_BATCH_PROC_MANAGEMENT", "배치 작업 관리",
            Array.Empty<FieldDefinition>(),
            DeleteQueryId: "SYS.DeleteBatchProcess",
            Layout: new SectionNode
            {
                Id = "sec-batch", Title = "배치 작업 정의(1차: 정의 관리 — 실행 엔진 후속)",
                Children = new LayoutNode[]
                {
                    new RowNode { Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 7, Children = new LayoutNode[]
                        {
                            new GridWidget { Id = "g-batch", QueryId = "SYS.BatchProcessList", Columns = new GridColumnDefinition[]
                            {
                                new("BATCH_ID", "배치 ID", Width: 140), new("BATCH_NAME", "배치명"),
                                new("BATCH_TYPE", "유형", Width: 90), new("BATCH_RULE", "실행 룰"),
                                new("BATCH_OPTIONS", "옵션"), new("DESCRIPTION", "설명"),
                            } },
                        } },
                        new ColumnNode { Span = 5, Children = new LayoutNode[]
                        {
                            new FormWidget { Id = "f-batch", SaveQueryId = "SYS.UpsertBatchProcess", Fields = new FieldWidget[]
                            {
                                new() { FieldKey = "batchId", Field = new FieldDefinition("batchId", "배치 ID", FieldType.Text, Required: true) },
                                new() { FieldKey = "batchName", Field = new FieldDefinition("batchName", "배치명", FieldType.Text, Required: true) },
                                new() { FieldKey = "batchType", Field = new FieldDefinition("batchType", "유형", FieldType.Text) },
                                new() { FieldKey = "batchRule", Field = new FieldDefinition("batchRule", "실행 룰 ID", FieldType.Text) },
                                new() { FieldKey = "batchOptions", Field = new FieldDefinition("batchOptions", "스케줄/옵션", FieldType.Text) },
                                new() { FieldKey = "batchInputData", Field = new FieldDefinition("batchInputData", "입력 파라미터", FieldType.Text) },
                                new() { FieldKey = "description", Field = new FieldDefinition("description", "설명", FieldType.Text) },
                            } },
                            new ButtonWidget { Id = "b-batch-save", Label = "저장", Command = "SYS.UpsertBatchProcess", RequiredPermission = "sys:manage" },
                            new ButtonWidget { Id = "b-batch-del", Label = "삭제", Command = "SYS.DeleteBatchProcess", RequiredPermission = "sys:manage", ConfirmMessage = "입력한 배치 ID의 정의를 삭제(비활성)하시겠습니까?" },
                        } },
                    } },
                    // 실행 이력(V068) — 배치 엔진(수동 run·주기 워커)이 기록. 검색 조건 @batchId로 필터.
                    new RowNode { Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 12, Children = new LayoutNode[]
                        {
                            new TextWidget { Id = "t-batch-hist", Text = "실행 이력(최근 200건)", IsLabel = true },
                            new GridWidget { Id = "g-batch-hist", QueryId = "SYS.BatchProcessHistoryList", Columns = new GridColumnDefinition[]
                            {
                                new("STARTED_AT", "시작", Width: 150), new("FINISHED_AT", "종료", Width: 150),
                                new("BATCH_ID", "배치 ID", Width: 150), new("SUCCESS", "성공", Width: 60),
                                new("AFFECTED", "처리 행", Width: 80), new("ERROR_MESSAGE", "오류"), new("EXECUTED_BY", "실행자", Width: 100),
                            } },
                        } },
                    } },
                },
            },
            SearchFields: new FieldDefinition[]
            {
                new("batchId", "배치 ID"),
            }));

        // 시스템관리 — 메뉴별 권한 관리(SYSTEM_2_MENU_AUTH_MANAGEMENT). SYS_MENU_ROLE(V031) 매핑 CRUD.
        // 가시성 의미론: 미매핑 메뉴=공개(하위호환), 매핑 메뉴=역할 일치 시만 사이드바 노출(SYS.MenuTreeForUser).
        Register(new ScreenDefinition("SYSTEM_2_MENU_AUTH_MANAGEMENT", "메뉴별 권한 관리",
            Array.Empty<FieldDefinition>(),
            DeleteQueryId: "SYS.DeleteMenuRole",
            Layout: new SectionNode
            {
                Id = "sec-menurole", Title = "메뉴-역할 매핑(미매핑 메뉴=전체 공개, 매핑 시 해당 역할만 노출)",
                Children = new LayoutNode[]
                {
                    new RowNode { Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 7, Children = new LayoutNode[]
                        {
                            new GridWidget { Id = "g-menurole", QueryId = "SYS.MenuRoleList", Columns = new GridColumnDefinition[]
                            {
                                new("MENU_ID", "메뉴 ID", Width: 200), new("MENU_NAME", "메뉴명"),
                                new("ROLE_ID", "역할 ID", Width: 120), new("ROLE_NAME", "역할명"),
                            } },
                        } },
                        new ColumnNode { Span = 5, Children = new LayoutNode[]
                        {
                            new FormWidget { Id = "f-menurole", SaveQueryId = "SYS.UpsertMenuRole", Fields = new FieldWidget[]
                            {
                                // 동적 Select — 실존 메뉴/역할만 선택 가능(오타 매핑·FK 위반 방지). 첫 컬럼=값, 둘째=라벨.
                                new() { FieldKey = "menuId", Field = new FieldDefinition("menuId", "메뉴", FieldType.Select, Required: true, OptionsQueryId: "SYS.MenuTree") },
                                new() { FieldKey = "roleId", Field = new FieldDefinition("roleId", "역할", FieldType.Select, Required: true, OptionsQueryId: "SYS.ListRoles") },
                            } },
                            new ButtonWidget { Id = "b-menurole-save", Label = "저장", Command = "SYS.UpsertMenuRole", RequiredPermission = "sys:manage" },
                            new ButtonWidget { Id = "b-menurole-del", Label = "삭제", Command = "SYS.DeleteMenuRole", RequiredPermission = "sys:manage", ConfirmMessage = "선택한 메뉴-역할 매핑을 삭제하시겠습니까? 마지막 매핑을 지우면 해당 메뉴는 전체 공개로 돌아갑니다." },
                        } },
                    } },
                },
            }));

        // FDC — VIRTUAL EVENT 관리(EES_FDC_VIRTUAL_EVENT_MANAGEMENT, V067). 레거시 FDC_TB_VIRTUAL_EVENT_PARAMETER 포팅.
        // 1차 범위 = 정의 CRUD까지 — 평가 엔진(CONDITION_FORMULA 판정·이벤트 데이터 수집)은 FDC 워커 후속.
        Register(new ScreenDefinition("EES_FDC_VIRTUAL_EVENT_MANAGEMENT", "VIRTUAL EVENT 관리",
            Array.Empty<FieldDefinition>(),
            DeleteQueryId: "FDC.DeleteVirtualEvent",
            Layout: new SectionNode
            {
                Id = "sec-ve", Title = "가상 이벤트 정의(1차: 정의 관리 — 평가 엔진 후속)",
                Children = new LayoutNode[]
                {
                    new RowNode { Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 7, Children = new LayoutNode[]
                        {
                            new GridWidget { Id = "g-ve", QueryId = "FDC.VirtualEventList", Columns = new GridColumnDefinition[]
                            {
                                new("EQUIPMENT_ID", "설비", Width: 110), new("EVENT_ID", "이벤트 ID", Width: 130),
                                new("EVENT_NAME", "이벤트명"), new("EVENT_ON", "ON"), new("EVENT_OFF", "OFF"),
                                new("CONDITION_FORMULA", "조건 수식"),
                            } },
                        } },
                        new ColumnNode { Span = 5, Children = new LayoutNode[]
                        {
                            new FormWidget { Id = "f-ve", SaveQueryId = "FDC.UpsertVirtualEvent", Fields = new FieldWidget[]
                            {
                                new() { FieldKey = "plantId", Field = new FieldDefinition("plantId", "공장 ID", FieldType.Text, Required: true) },
                                new() { FieldKey = "equipmentId", Field = new FieldDefinition("equipmentId", "설비 ID", FieldType.Text, Required: true) },
                                new() { FieldKey = "eventId", Field = new FieldDefinition("eventId", "이벤트 ID", FieldType.Text, Required: true) },
                                new() { FieldKey = "eventName", Field = new FieldDefinition("eventName", "이벤트명", FieldType.Text, Required: true) },
                                new() { FieldKey = "eventOn", Field = new FieldDefinition("eventOn", "ON 판정", FieldType.Text) },
                                new() { FieldKey = "eventOff", Field = new FieldDefinition("eventOff", "OFF 판정", FieldType.Text) },
                                new() { FieldKey = "conditionFormula", Field = new FieldDefinition("conditionFormula", "조건 수식", FieldType.Text) },
                                new() { FieldKey = "description", Field = new FieldDefinition("description", "설명", FieldType.Text) },
                            } },
                            new ButtonWidget { Id = "b-ve-save", Label = "저장", Command = "FDC.UpsertVirtualEvent", RequiredPermission = "fdc:manage" },
                            new ButtonWidget { Id = "b-ve-del", Label = "삭제", Command = "FDC.DeleteVirtualEvent", RequiredPermission = "fdc:manage", ConfirmMessage = "입력한 설비/이벤트의 가상 이벤트 정의를 삭제(비활성)하시겠습니까?" },
                        } },
                    } },
                    // 전이 이력(V069) — 평가 엔진이 상태 전이 시에만 기록. 검색 조건 @equipmentId/@eventId 필터.
                    new RowNode { Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 12, Children = new LayoutNode[]
                        {
                            new TextWidget { Id = "t-ve-hist", Text = "전이 이력(최근 200건)", IsLabel = true },
                            new GridWidget { Id = "g-ve-hist", QueryId = "FDC.VirtualEventHistoryList", Columns = new GridColumnDefinition[]
                            {
                                new("EVALUATED_AT", "평가 시각", Width: 150), new("EQUIPMENT_ID", "설비", Width: 110),
                                new("EVENT_ID", "이벤트 ID", Width: 130), new("EVENT_STATE", "상태", Width: 70),
                                new("FORMULA", "수식"), new("DETAILS", "상세"),
                            } },
                        } },
                    } },
                },
            },
            SearchFields: new FieldDefinition[]
            {
                new("equipmentId", "설비 ID"),
                new("eventId", "이벤트 ID"),
            }));

        // FDC — 팝업 모니터링 대시보드(EES_POPUP_MONITORING_DASHBOARD, 실시간 v3 완결) : 10초 폴링 + 실시간 이벤트 푸시.
        // RefreshIntervalSeconds>0 화면은 IScreenRefreshNotifier(실시간 v3)를 자동 구독해 도메인 이벤트 시
        // 즉시 재조회(1초 스로틀)하고, 폴링 주기는 이벤트 부재 시 폴백이다. 트렌드 차트는 RadzenChart.
        Register(new ScreenDefinition("EES_POPUP_MONITORING_DASHBOARD", "설비 모니터링(실시간)",
            Array.Empty<FieldDefinition>(),
            RefreshIntervalSeconds: 10,
            Layout: new SectionNode
            {
                Id = "sec-mon", Title = "설비 모니터링 — 10초 자동 새로고침",
                Children = new LayoutNode[]
                {
                    new RowNode { Id = "mon-kpi", Children = new LayoutNode[]
                    {
                        DashKpi("mon-alarm", "활성 알람", "ACTIVE_ALARMS"),
                        DashKpi("mon-wo", "진행 작업지시", "OPEN_WORK_ORDERS"),
                        DashKpi("mon-plan", "가동 생산계획", "ACTIVE_PLANS"),
                    } },
                    new RowNode { Id = "mon-grids", Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 6, Children = new LayoutNode[]
                        {
                            new TextWidget { Id = "t-state", Text = "설비 현재 상태", IsLabel = true },
                            new GridWidget { Id = "g-state", QueryId = "EST.CurrentStateList", Columns = new GridColumnDefinition[]
                            {
                                new("EQUIPMENT_ID", "설비", Width: 120), new("CURRENT_STATE_ID", "상태", Width: 90),
                                new("PLANT_ID", "공장", Width: 90), new("STATE_CHANGED_AT", "변경 시각"),
                            } },
                        } },
                        new ColumnNode { Span = 6, Children = new LayoutNode[]
                        {
                            new TextWidget { Id = "t-alarm", Text = "설비 알람", IsLabel = true },
                            new GridWidget { Id = "g-alarm", QueryId = "EST.EquipmentAlarmList", Columns = new GridColumnDefinition[]
                            {
                                new("EQUIPMENT_ID", "설비", Width: 120), new("ALARM_CODE", "코드", Width: 100),
                                new("ALARM_NAME", "알람명"), new("ALARM_LEVEL", "레벨", Width: 80), new("OCCURRED_AT", "발생 시각"),
                            } },
                        } },
                    } },
                    new RowNode { Id = "mon-trend", Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 12, Children = new LayoutNode[]
                        {
                            // 최근 수집값 트렌드(최신 500행 중 마지막 60포인트) — 자동 새로고침과 조합해 준실시간 라인.
                            new TrendChartWidget { Id = "c-trend", Label = "FDC 수집값 트렌드(최근)", QueryId = "FDC.CollectDataList", ValueColumn = "VALUE", MaxPoints = 60, TimeColumn = "COLLECTED_AT" },
                        } },
                    } },
                    new RowNode { Id = "mon-ilk", Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 12, Children = new LayoutNode[]
                        {
                            new TextWidget { Id = "t-ilk", Text = "최근 인터락 이력", IsLabel = true },
                            new GridWidget { Id = "g-ilk", QueryId = "FDC.InterlockHistoryList", Columns = new GridColumnDefinition[]
                            {
                                new("EQUIPMENT_ID", "설비", Width: 120), new("RULE_ID", "규칙", Width: 120),
                                new("PARAMETER_ID", "파라미터", Width: 120), new("ACTION", "조치", Width: 100),
                                new("TRIGGER_VALUE", "발생 값", Width: 110), new("MESSAGE", "메시지"),
                            } },
                        } },
                    } },
                },
            },
            Purpose: ScreenPurpose.Report));

        // 시스템관리 — 콘텐츠 매핑 서비스 관리(SYSTEM2_CONTENTMAPPINGSERVICE_MANAGEMENT): 아키텍처 대체 안내로 점등.
        // 레거시 화면↔서비스 매핑 테이블(SYS_TB_CONTENT_MAPPING_SERVICE)의 역할은 통합 호스트에서 화면정의
        // (QueryId/SaveQueryId 바인딩) + 명명 쿼리 레지스트리가 대체한다 — 별도 매핑 관리 화면은 신설하지 않는다.
        Register(new ScreenDefinition("SYSTEM2_CONTENTMAPPINGSERVICE_MANAGEMENT", "콘텐츠 매핑 서비스 관리(대체됨)",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Id = "sec-cms", Title = "이 기능은 새 아키텍처로 대체되었습니다",
                Children = new LayoutNode[]
                {
                    new RowNode { Children = new LayoutNode[] { new ColumnNode { Span = 12, Children = new LayoutNode[]
                    {
                        new TextWidget { Id = "t-cms-1", Text = "레거시의 화면↔서비스 매핑(SYS_TB_CONTENT_MAPPING_SERVICE)은 통합 호스트에서 화면 정의(QueryId/SaveQueryId 바인딩)와 명명 쿼리 레지스트리(config/db/queries)가 대체합니다." },
                        new TextWidget { Id = "t-cms-2", Text = "매핑 현황은 시스템 관리 > S/O 관리(메타 카탈로그)에서 조회할 수 있습니다." },
                    } } } },
                },
            }));

        // ===== SmartUX MDM 업무화면 점등(Phase 2) — 실제 SmartUX 메뉴 잎(menuId=UI_ID)에 기존 명명쿼리를 바인딩한다.
        // 사이드바 MDM 폴더의 해당 잎을 클릭하면 '준비 중' 대신 실제 그리드/폼이 렌더된다. 백엔드(테이블·명명쿼리)가
        // 있는 화면만 점등(나머지는 '준비 중' 유지). =====

        // 공장 관리(FACTORY_MDM_PLANT) — 좌측 공장 그리드(MDM.PlantList) + 우측 등록 폼/저장(MDM.CreatePlant). 실동작 CRUD.
        Register(new ScreenDefinition("FACTORY_MDM_PLANT", "공장 관리",
            Array.Empty<FieldDefinition>(),
            DeleteQueryId: "MDM.DeletePlant",
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
            QueryId: "QMS.SpcParamList", Purpose: ScreenPurpose.Report));

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
        Register(new ScreenDefinition("FACTORY_EMS_STD_SPARE_PART", "Spare Part 마스터",
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
            QueryId: "EMS.SparePartsAll", Purpose: ScreenPurpose.Inquiry));

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
            QueryId: "FDC.InterlockHistoryList", Purpose: ScreenPurpose.Inquiry));

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
            QueryId: "FDC.CollectDataList", Purpose: ScreenPurpose.Report));

        // FDC 관심 데이터 차트(EES_FDC_INTERESTED_DATA_CHART) — 동일 수집 시계열(관심 파라미터 뷰).
        Register(new ScreenDefinition("EES_FDC_INTERESTED_DATA_CHART", "FDC 관심 데이터 차트",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("PARAMETER_ID", "파라미터 ID"), new("VALUE", "측정값"),
                new("COLLECTED_AT", "수집시각"), new("QUALITY", "품질"), new("LOWER_LIMIT", "하한"), new("UPPER_LIMIT", "상한"),
            },
            QueryId: "FDC.CollectDataList", Purpose: ScreenPurpose.Report));

        // 실시간 데이터 차트(EES_FDC_REAL_TIME_TRACE_PARA_MONITORING) — 수집 시계열 최근값(실시간 모니터링).
        Register(new ScreenDefinition("EES_FDC_REAL_TIME_TRACE_PARA_MONITORING", "실시간 데이터 차트",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("PARAMETER_ID", "파라미터 ID"), new("VALUE", "측정값"),
                new("COLLECTED_AT", "수집시각"), new("QUALITY", "품질"), new("LOWER_LIMIT", "하한"), new("UPPER_LIMIT", "상한"),
            },
            QueryId: "FDC.CollectDataList", Purpose: ScreenPurpose.Report));

        // ===== FDC 잔여 3화면 점등(2026-07-10) — 250/250 완결. =====
        // VIRTUAL EVENT 이력(V069) — 평가 엔진이 전이 시에만 기록. 백엔드(테이블·쿼리·워커) 기성, 화면만 부재였다.
        Register(new ScreenDefinition("EES_FDC_VIRTUAL_EVENT_HISTORY", "VIRTUAL EVENT 이력 조회",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("HISTORY_ID", "이력 ID", Width: 200), new("EQUIPMENT_ID", "설비 ID", Width: 110),
                new("EVENT_ID", "이벤트 ID", Width: 130), new("EVENT_STATE", "상태", Width: 90),
                new("FORMULA", "수식"), new("DETAILS", "상세"), new("EVALUATED_AT", "평가시각"),
            },
            QueryId: "FDC.VirtualEventHistoryList", Purpose: ScreenPurpose.Inquiry));

        // 사용자별 실시간 모니터링 — 충실판(V084): 내 관심 파라미터(등록 폼+목록+해제 일괄명령) 위에
        // 실시간 값 보드(JOIN, 10초 갱신). 전 쿼리 @currentUser 스코프(개인화 규약 — 타인 조작 불가).
        Register(new ScreenDefinition("EES_FDC_REAL_TIME_USER_MONITORING", "FDC 사용자별 실시간 모니터링",
            Array.Empty<FieldDefinition>(),
            RefreshIntervalSeconds: 10,
            BulkCommands: new BulkCommandDefinition[]
            {
                new("관심 해제", "FDC.DeleteUserParameter", "선택한 관심 파라미터를 해제할까요?"),
            },
            Layout: new SectionNode
            {
                Id = "sec-usermon", Title = "사용자별 실시간 모니터링(내 관심 파라미터)",
                Children = new LayoutNode[]
                {
                    new RowNode { Id = "um-form", Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 12, Children = new LayoutNode[]
                        {
                            new TextWidget { Id = "um-t0", Text = "관심 파라미터 등록", IsLabel = true },
                            new FormWidget
                            {
                                Id = "um-reg", SaveQueryId = "FDC.CreateUserParameter",
                                Fields = new FieldWidget[]
                                {
                                    new() { Id = "um-f1", Field = new FieldDefinition("equipmentId", "설비 ID", Required: true) },
                                    new() { Id = "um-f2", Field = new FieldDefinition("parameterId", "파라미터 ID", Required: true) },
                                    new() { Id = "um-f3", Field = new FieldDefinition("displaySequence", "표시 순서", FieldType.Number) },
                                },
                            },
                        } },
                    } },
                    new RowNode { Id = "um-list", Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 4, Children = new LayoutNode[]
                        {
                            new TextWidget { Id = "um-t1", Text = "내 관심 파라미터", IsLabel = true },
                            new GridWidget { Id = "um-g1", QueryId = "FDC.UserParameterList", Columns = new GridColumnDefinition[]
                            {
                                new("EQUIPMENT_ID", "설비 ID"), new("PARAMETER_ID", "파라미터 ID"), new("DISPLAY_SEQUENCE", "순서", Width: 80),
                            } },
                        } },
                        new ColumnNode { Span = 8, Children = new LayoutNode[]
                        {
                            new TextWidget { Id = "um-t2", Text = "실시간 값(10초 갱신)", IsLabel = true },
                            new GridWidget { Id = "um-g2", QueryId = "FDC.UserMonitoringData", Columns = new GridColumnDefinition[]
                            {
                                new("EQUIPMENT_ID", "설비 ID", Width: 110), new("PARAMETER_ID", "파라미터 ID", Width: 130),
                                new("VALUE", "측정값"), new("COLLECTED_AT", "수집시각"), new("QUALITY", "품질", Width: 90),
                                new("LOWER_LIMIT", "하한"), new("UPPER_LIMIT", "상한"),
                            } },
                        } },
                    } },
                },
            }));

        // 동종 설비간 동일성 검정(tool-to-tool matching) v1 — 파라미터×설비 분포 요약(건수·평균·범위·분산) 비교
        // 그리드. 통계 검정(t-test 등)·차트는 v2 분리(스카우트 권고).
        Register(new ScreenDefinition("EES_FDC_TOOL_TO_TOOL_MATCHING", "동종 설비간 FDC 동일성 검정",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PARAMETER_ID", "파라미터 ID", Width: 140), new("EQUIPMENT_ID", "설비 ID", Width: 110),
                new("N", "표본수", Width: 90), new("AVG_VALUE", "평균"), new("MIN_VALUE", "최소"),
                new("MAX_VALUE", "최대"), new("RANGE_VALUE", "범위"), new("VARIANCE_VALUE", "분산"),
                new("DIFF_FROM_GRAND", "전체평균 대비 편차"),
            },
            QueryId: "FDC.EquipmentParameterStats", Purpose: ScreenPurpose.Report));

        // FDC SUMMARY 데이터 차트(EES_FDC_SUMMARY_DATA_CHART) — 수집 시계열 요약 뷰.
        Register(new ScreenDefinition("EES_FDC_SUMMARY_DATA_CHART", "FDC SUMMARY 데이터 차트",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("PARAMETER_ID", "파라미터 ID"), new("VALUE", "측정값"),
                new("COLLECTED_AT", "수집시각"), new("QUALITY", "품질"), new("LOWER_LIMIT", "하한"), new("UPPER_LIMIT", "상한"),
            },
            QueryId: "FDC.CollectDataList", Purpose: ScreenPurpose.Report));

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
            QueryId: "EST.CurrentStateList", Purpose: ScreenPurpose.Report));

        // 공장 모니터링(EES_EPT_PLANT_MONITORING) — 공장 단위 설비 현재 상태 현황(동일 현재상태 뷰).
        Register(new ScreenDefinition("EES_EPT_PLANT_MONITORING", "공장 모니터링",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PLANT_ID", "공장"), new("EQUIPMENT_ID", "설비 ID"), new("CURRENT_STATE_ID", "현재 상태"),
                new("STATE_CHANGED_AT", "상태변경시각"), new("STATE_VERSION", "버전"),
            },
            QueryId: "EST.CurrentStateList", Purpose: ScreenPurpose.Report));

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
            QueryId: "EST.StateHistoryList", Purpose: ScreenPurpose.Inquiry));

        // 설비 이벤트 이력(EES_EPT_EQUIPMENT_EVENT_HISTORY) — 상태 전이를 이벤트 로그로 조회(동일 상태이력 뷰).
        Register(new ScreenDefinition("EES_EPT_EQUIPMENT_EVENT_HISTORY", "설비 이벤트 이력",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("FROM_STATE", "이전 상태"), new("TO_STATE", "변경 상태"),
                new("CHANGED_AT", "발생시각"), new("CHANGED_BY", "발생자"), new("SOURCE_TYPE", "출처"), new("REASON", "사유"),
            },
            QueryId: "EST.StateHistoryList", Purpose: ScreenPurpose.Inquiry));

        // 설비 가동 이력(EES_EPT_EQUIPMENT_PRODUCTIVE_HISTORY) — 상태 변경 이력을 가동 관점으로 조회(동일 상태이력 뷰).
        Register(new ScreenDefinition("EES_EPT_EQUIPMENT_PRODUCTIVE_HISTORY", "설비 가동 이력",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("TO_STATE", "가동 상태"), new("CHANGED_AT", "변경시각"),
                new("CHANGED_BY", "변경자"), new("SOURCE_TYPE", "출처"), new("REASON", "사유"),
            },
            QueryId: "EST.StateHistoryList", Purpose: ScreenPurpose.Inquiry));

        // 설비 알람 이력(EES_EPT_EQUIPMENT_ALARM_HISTORY) — 설비 알람(EST.EquipmentAlarmList).
        Register(new ScreenDefinition("EES_EPT_EQUIPMENT_ALARM_HISTORY", "설비 알람 이력",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("ALARM_CODE", "알람 코드"), new("ALARM_NAME", "알람명"),
                new("ALARM_LEVEL", "등급"), new("OCCURRED_AT", "발생시각"), new("CLEARED_AT", "해제시각"), new("ELAPSED_SECONDS", "지속(초)"),
            },
            QueryId: "EST.EquipmentAlarmList", Purpose: ScreenPurpose.Inquiry));

        // 알람 발생 이력(EES_EPT_ALARM_HISTORY) — 동일 설비 알람 이력 뷰.
        Register(new ScreenDefinition("EES_EPT_ALARM_HISTORY", "알람 발생 이력",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("ALARM_CODE", "알람 코드"), new("ALARM_NAME", "알람명"),
                new("ALARM_LEVEL", "등급"), new("OCCURRED_AT", "발생시각"), new("CLEARED_AT", "해제시각"), new("ELAPSED_SECONDS", "지속(초)"),
            },
            QueryId: "EST.EquipmentAlarmList", Purpose: ScreenPurpose.Inquiry));

        // WORST10 알람(EES_EPT_WORST10_ALARM) — 설비별 알람 발생 건수 상위 10(EST.WorstAlarmEquipment 집계).
        Register(new ScreenDefinition("EES_EPT_WORST10_ALARM", "WORST10 알람",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("ALARM_COUNT", "알람 건수"), new("LAST_OCCURRED_AT", "최근 발생시각"),
            },
            QueryId: "EST.WorstAlarmEquipment", Purpose: ScreenPurpose.Report));

        // ===== SmartUX EPT OEE(설비종합효율) 점등(Phase 4) — V050 마트. OEE=가용성×성능×품질. 사전집계 마트 read
        // (원자료→마트 집계는 배치/워커 소관, 후속). 비율 컬럼(AVAILABILITY/PERFORMANCE/QUALITY/OEE)은 분율(0~1). =====

        // 설비 종합 지표(EES_EPT_OVERALL_EQUIPMENT_EFFECTIVENESS) — 설비×일자 OEE 마트(EST.OeeSummaryList).
        Register(new ScreenDefinition("EES_EPT_OVERALL_EQUIPMENT_EFFECTIVENESS", "설비 종합 지표(OEE)",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("OEE_DATE", "일자"), new("EQUIPMENT_ID", "설비 ID"), new("PLANT_ID", "공장"),
                new("AVAILABILITY_PERCENT", "가동률/OEE 가용성 (%)"), new("PERFORMANCE_PERCENT", "성능가동률 (%)"),
                new("QUALITY_PERCENT", "양품률 (%)"), new("OEE_PERCENT", "OEE (%)"),
                new("PLANNED_MINUTES", "계획(분)"), new("DOWNTIME_MINUTES", "비가동(분)"),
                new("TOTAL_COUNT", "총생산"), new("GOOD_COUNT", "양품"),
            },
            QueryId: "EST.OeeSummaryList", Purpose: ScreenPurpose.Report));

        Register(new ScreenDefinition("EES_EPT_TAKT_TIME", "택트타임 및 실제 사이클",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("TAKT_DATE", "기준일"), new("PLANT_ID", "공장"), new("PRODUCT_ID", "제품"),
                new("PROCESS_ID", "공정"), new("EQUIPMENT_ID", "설비"), new("SHIFT_ID", "작업조"),
                new("TARGET_TAKT_SECONDS_PER_UNIT", "목표 택트 (s/unit)"),
                new("IDEAL_CYCLE_SECONDS_PER_UNIT", "이상 사이클 (s/unit)"),
                new("ACTUAL_CYCLE_SECONDS_PER_UNIT", "실제 사이클 (s/unit)"),
                new("DEVIATION_SECONDS_PER_UNIT", "목표 대비 편차 (s/unit)"),
                new("DEVIATION_PERCENT", "목표 대비 편차 (%)"),
                new("UTILIZATION_PERCENT", "가동률 (%, OEE Availability)"),
                new("REQUIRED_QTY", "요구수량"), new("ACTUAL_QTY", "TrackOut 실적"),
                new("MEASURED_QTY", "사이클 측정수량"), new("QUANTITY_UOM", "수량 UOM"),
                new("NET_AVAILABLE_SECONDS", "순가용시간 (s)"), new("ACTUAL_RUN_SECONDS", "측정 가동시간 (s)"),
            },
            QueryId: "EST.TaktSummaryList", Purpose: ScreenPurpose.Report));

        Register(new ScreenDefinition("EPT_STD_TAKT_TARGET", "택트타임 목표 관리",
            new FieldDefinition[]
            {
                new("taktTargetId", "목표 ID", Required: true), new("plantId", "공장", Required: true),
                new("productId", "제품", Required: true), new("processId", "공정", Required: true),
                new("equipmentId", "설비", Required: true), new("shiftId", "작업조 (공백=일전체)"),
                new("effectiveFrom", "유효 시작", Required: true), new("effectiveTo", "유효 종료"),
                new("requiredQty", "요구수량", FieldType.Number, Required: true),
                new("netAvailableSeconds", "순가용시간 (s)", FieldType.Number, Required: true),
                new("idealCycleSecondsPerUnit", "이상 사이클 (s/unit)", FieldType.Number, Required: true),
                new("quantityUom", "수량 UOM", Required: true),
                new("timeUom", "시간 UOM", FieldType.Select, Required: true, Options: new[] { "s/unit" }),
                new("description", "설명"),
            },
            new GridColumnDefinition[]
            {
                new("TAKT_TARGET_ID", "목표 ID"), new("PLANT_ID", "공장"), new("PRODUCT_ID", "제품"),
                new("PROCESS_ID", "공정"), new("EQUIPMENT_ID", "설비"), new("SHIFT_ID", "작업조"),
                new("REQUIRED_QTY", "요구수량"), new("NET_AVAILABLE_SECONDS", "순가용시간 (s)"),
                new("IDEAL_CYCLE_SECONDS_PER_UNIT", "이상 사이클 (s/unit)"), new("QUANTITY_UOM", "수량 UOM"),
                new("TIME_UOM", "시간 UOM"), new("EFFECTIVE_FROM", "유효 시작"), new("EFFECTIVE_TO", "유효 종료"),
            }, QueryId: "EST.TaktTargetList", SaveQueryId: "EST.SaveTaktTarget", DeleteQueryId: "EST.DeleteTaktTarget",
            Purpose: ScreenPurpose.Manage));

        // 설비 유실 분석(EES_EPT_EQUIPMENT_LOSS_ANALYSIS) — 6대 손실 카테고리별 손실 집계(EST.LossByCategory).
        Register(new ScreenDefinition("EES_EPT_EQUIPMENT_LOSS_ANALYSIS", "설비 유실 분석",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("LOSS_CATEGORY", "손실 유형"), new("LOSS_COUNT", "발생 건수"), new("TOTAL_MINUTES", "총 손실(분)"),
            },
            QueryId: "EST.LossByCategory", Purpose: ScreenPurpose.Report));

        // WORST5 유실(EES_EPT_WORST5_LOSS) — 설비별 총 손실 시간 상위 5(EST.WorstLossEquipment 집계).
        Register(new ScreenDefinition("EES_EPT_WORST5_LOSS", "WORST5 유실",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("TOTAL_MINUTES", "총 손실(분)"), new("LOSS_COUNT", "손실 건수"),
            },
            QueryId: "EST.WorstLossEquipment", Purpose: ScreenPurpose.Report));

        // 관심 지표 등록(EES_EPT_INTERESTED_INDEX_MANAGEMENT) — KPI 지표 마스터(EST.IndexList) + 등록 폼(EST.CreateIndex).
        Register(new ScreenDefinition("EES_EPT_INTERESTED_INDEX_MANAGEMENT", "관심 지표 등록",
            new FieldDefinition[]
            {
                new("indexId", "지표 ID", Required: true), new("indexName", "지표명", Required: true),
                new("indexCategory", "분류"), new("unit", "단위"), new("description", "설명"),
            },
            new GridColumnDefinition[]
            {
                new("INDEX_ID", "지표 ID"), new("INDEX_NAME", "지표명"), new("INDEX_CATEGORY", "분류"),
                new("UNIT", "단위"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "EST.IndexList",
            SaveQueryId: "EST.CreateIndex", DeleteQueryId: "EST.DeleteIndex"));

        // 지표 관리(EPT_STD_INDEX_MGNT) — 동일 KPI 지표 마스터 뷰.
        Register(new ScreenDefinition("EPT_STD_INDEX_MANAGEMENT", "지표 관리",
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
            QueryId: "EST.IndexValueList", Purpose: ScreenPurpose.Inquiry));

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
            QueryId: "POM.ProductionOrderList", Purpose: ScreenPurpose.Report));

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

        // LOT 보류(FACTORY_WPM_LOT_HOLD)·보류 해제(FACTORY_WPM_LOT_HOLD_RELEASE) — 보류 상태 LOT 조회(POM.LotHoldList).
        Register(new ScreenDefinition("FACTORY_WPM_LOT_HOLD", "LOT 보류",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("LOT_ID", "LOT ID"), new("PLANT_ID", "공장"), new("PRODUCT_ID", "품목"), new("QTY", "수량"),
                new("LOT_STATE", "LOT상태"), new("PROCESS_STATE", "공정상태"), new("EQUIPMENT_ID", "설비"), new("IS_HOLD", "홀드"),
            },
            QueryId: "POM.LotHoldList"));
        Register(new ScreenDefinition("FACTORY_WPM_LOT_HOLD_RELEASE", "LOT 보류 해제",
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
            QueryId: "POM.YieldByProduct", Purpose: ScreenPurpose.Report));

        // LOT 추적(FACTORY_RPT_LOT_TRACE) — Lot 이력 조회(POM.LotTraceList).
        Register(new ScreenDefinition("FACTORY_RPT_LOT_TRACE", "LOT 추적",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("LOT_ID", "LOT ID"), new("PLANT_ID", "공장"), new("EQUIPMENT_ID", "설비"), new("PROCESS_ID", "공정"),
                new("TRACK_IN_TIME", "In시각"), new("TRACK_OUT_TIME", "Out시각"), new("EXECUTION_ID", "실행"),
                new("QTY", "수량"), new("DEFECT_QTY", "불량"), new("LOT_STATE", "LOT상태"),
            },
            QueryId: "POM.LotTraceList", Purpose: ScreenPurpose.Inquiry));

        // 작업조 관리(MES_MDM_COM_SHIFT) — 작업조 마스터 조회(MDM.ShiftList) + 등록 폼(MDM.CreateShift).
        Register(new ScreenDefinition("MES_MDM_COM_SHIFT", "작업조 관리",
            new FieldDefinition[]
            {
                new("shiftId", "작업조 ID", Required: true), new("shiftName", "작업조명", Required: true),
                new("startTime", "시작(HH:mm)"), new("endTime", "종료(HH:mm)"),
            },
            new GridColumnDefinition[]
            {
                new("SHIFT_ID", "작업조 ID"), new("SHIFT_NAME", "작업조명"), new("START_TIME", "시작"),
                new("END_TIME", "종료"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "MDM.ShiftList",
            SaveQueryId: "MDM.CreateShift", DeleteQueryId: "MDM.DeleteShift", Purpose: ScreenPurpose.Manage));

        // ===== SmartUX FACTORY_QCA(품질검사) 점등 — 기존 QMS 검사 도메인(V037/V040)으로 전수 재사용, 마이그레이션 0.
        // FACTORY_QCA는 QMS 검사(수입/공정/출하·정의·항목·방법·규격)로 향하는 다른 메뉴 경로다. =====
        RegisterQcaInspection("FACTORY_QCA_IMPORT_INSPECTION", "수입검사 관리", "QMS.IncomingInspectionList", QmsInspectionMetaCommands.RecordIncoming);
        RegisterQcaInspection("FACTORY_QCA_REPORT_IMPORT_INSPECTION_STATUS", "수입검사 현황", "QMS.IncomingInspectionList");
        RegisterQcaInspection("FACTORY_QCA_SEGMENT_INSPECTION", "공정검사 관리", "QMS.ProcessInspectionList", QmsInspectionMetaCommands.RecordProcess);
        RegisterQcaInspection("FACTORY_QCA_REPORT_SEGMENT_INSPECTION_STATUS", "공정검사 현황", "QMS.ProcessInspectionList");
        RegisterQcaInspection("FACTORY_QCA_DELIVERY_INSPECTION", "출하검사 관리", "QMS.ShippingInspectionList", QmsInspectionMetaCommands.RecordShipping);
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

        // 수입검사 정보 연결(FACTORY_QCA_IMPORT_INSPECTION_MAPPING) — 수입검사 방법 설정(QMS.IncomingInspMethodList).
        Register(new ScreenDefinition("FACTORY_QCA_IMPORT_INSPECTION_MAPPING", "수입검사 정보 연결",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("METHOD_ID", "방법 ID"), new("METHOD_NAME", "방법명"), new("PRODUCT_ID", "품목"),
                new("SAMPLING_TYPE", "샘플링"), new("AQL_LEVEL", "AQL"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "QMS.IncomingInspMethodList"));

        // 공정/출하검사 정보 연결(FACTORY_QCA_{PROCESS,SHIPMENT}_INSPECTION_MAPPING) — 검사 규격 카탈로그(QMS.InspectionSpecList).
        RegisterQcaSpecMapping("FACTORY_QCA_PROCESS_INSPECTION_MAPPING", "공정검사 정보 연결");
        RegisterQcaSpecMapping("FACTORY_QCA_SHIPMENT_INSPECTION_MAPPING", "출하검사 정보 연결");

        // ===== SmartUX FACTORY_PRC(구매) 점등 — 레거시 PRC_TB_PURCHASE_ORDER를 V052로 단순 포팅. 이동오더는 후속(IVT 이동 모델). =====
        // 구매오더 관리(FACTORY_PRC_PURCHASE_ORDER, 등록 폼 포함)·구매오더 현황(REPORT, 조회 전용) — 발주 헤더(PRC.PurchaseOrderList).
        var prcOrderCols = new GridColumnDefinition[]
        {
            new("PURCHASE_ORDER_ID", "발주 ID"), new("PURCHASE_ORDER_NAME", "발주명"), new("PLANT_ID", "공장"),
            new("VENDOR_ID", "거래처"), new("ORDER_DATE", "발주일"), new("INCOMING_DATE", "입고예정일"),
            new("ORDER_QTY", "발주수량"), new("OWNER_ID", "담당자"), new("STATUS", "상태"), new("IS_HOLD", "홀드"),
        };
        Register(new ScreenDefinition("FACTORY_PRC_PURCHASE_ORDER", "구매오더 관리",
            new FieldDefinition[]
            {
                new("purchaseOrderId", "발주 ID", Required: true), new("plantId", "공장", Required: true),
                new("purchaseOrderName", "발주명"), new("vendorId", "거래처"),
                new("orderQty", "발주수량", FieldType.Number, Required: true),
            },
            prcOrderCols, QueryId: "PRC.PurchaseOrderList", SaveQueryId: "PRC.CreatePurchaseOrder", DeleteQueryId: "PRC.DeletePurchaseOrder",
            BulkCommands: new BulkCommandDefinition[]
            {
                new("발주", "PRC.OrderPurchaseOrder"),  // Draft→Ordered(가드)
                new("마감", "PRC.ClosePurchaseOrder"),  // Ordered/Incoming→Closed(가드)
            }));
        Register(new ScreenDefinition("FACTORY_PRC_REPORT_PURCHASEORDER", "구매오더 현황",
            Array.Empty<FieldDefinition>(), prcOrderCols, QueryId: "PRC.PurchaseOrderList", Purpose: ScreenPurpose.Report));

        // ===== SmartUX FACTORY_SLS(영업) 점등 — 레거시 SLS_TB_SALES_ORDER/REQUEST를 V053으로 포팅. 출하현황은 SHP 재사용. =====
        // 수주 관리(FACTORY_SLS_SALES_ORDER) — 수주 헤더(SLS.SalesOrderList) + 등록 폼(SLS.CreateSalesOrder).
        Register(new ScreenDefinition("FACTORY_SLS_SALES_ORDER", "수주 관리",
            new FieldDefinition[]
            {
                new("salesOrderId", "수주 번호", Required: true),
                new("plantId", "공장", FieldType.Select, Required: true, OptionsQueryId: "MDM.PlantCombo"),
                new("salesOrderName", "수주명", Required: true),
                new("customerId", "고객", FieldType.Select, Required: true, OptionsQueryId: "MDM.CustomerCombo"),
                new("productId", "품목", FieldType.Select, Required: true, OptionsQueryId: "MDM.ProductCombo"),
                new("planStartDate", "계획 시작일", FieldType.Date),
                new("planEndDate", "납기 예정일", FieldType.Date, Required: true),
                new("planQty", "계획수량", FieldType.Number, Required: true),
            },
            new GridColumnDefinition[]
            {
                // 카드 보기는 기본키를 제외한 앞 6개를 요약하므로, 업무 판단 핵심(고객·품목·납기·상태·수량)을 앞에 둔다.
                new("SALES_ORDER_ID", "수주 번호"), new("SALES_ORDER_NAME", "수주명"), new("CUSTOMER_ID", "고객"),
                new("PRODUCT_ID", "품목"), new("PLAN_END_DATE", "납기 예정일"), new("STATUS", "상태"),
                new("PLAN_QTY", "계획수량"), new("PLANT_ID", "공장"), new("PLAN_START_DATE", "계획 시작일"),
                new("DELIVERED_QTY", "납품수량"), new("IS_HOLD", "홀드"),
            },
            QueryId: "SLS.SalesOrderList",
            SaveQueryId: "SLS.CreateSalesOrder", DeleteQueryId: "SLS.DeleteSalesOrder",
            SearchFields: new FieldDefinition[]
            {
                new("plantId", "공장", FieldType.Select, OptionsQueryId: "MDM.PlantCombo"),
                new("customerId", "고객", FieldType.Select, OptionsQueryId: "MDM.CustomerCombo"),
                new("status", "상태", FieldType.Select,
                    Options: new[] { "Draft", "Confirmed", "Producing", "Delivered", "Closed" }),
            },
            BulkCommands: new BulkCommandDefinition[]
            {
                new("확정", "SLS.ConfirmSalesOrder"),   // Draft→Confirmed(가드)
                new("마감", "SLS.CloseSalesOrder"),     // Producing/Delivered→Closed(가드)
            },
            Purpose: ScreenPurpose.Manage));

        // 판매 요청(FACTORY_SLS_SALES_REQUEST) — 판매 요청 목록(SLS.SalesRequestList).
        Register(new ScreenDefinition("FACTORY_SLS_SALES_REQUEST", "판매 요청",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("SALES_REQUEST_ID", "요청 ID"), new("SALES_REQUEST_NAME", "요청명"), new("SALES_ORDER_ID", "수주 번호"),
                new("CUSTOMER_ID", "고객"), new("PRODUCT_ID", "품목"), new("REQUEST_DATE", "요청일"),
                new("REQUEST_QTY", "요청수량"), new("STATUS", "상태"),
            },
            QueryId: "SLS.SalesRequestList"));

        // 출하 현황(FACTORY_SLS_REPORT_DELIVERY) — 출하 이력 재사용(SHP.ShipmentHistoryList).
        Register(new ScreenDefinition("FACTORY_SLS_REPORT_DELIVERY", "출하 현황",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("HISTORY_ID", "이력 ID"), new("DELIVERY_ORDER_ID", "출하지시"), new("SHIPPED_AT", "출하시각"),
                new("SHIPPED_QTY", "출하수량"), new("SHIPPED_BY", "출하자"), new("CARRIER", "운송사"), new("TRACKING_NO", "송장번호"),
            },
            QueryId: "SHP.ShipmentHistoryList", Purpose: ScreenPurpose.Report));

        // ===== SmartUX FACTORY_STD 라벨 점등 — 레거시 STD_TB_LABEL* 를 V054(MDM_LABEL*)로 포팅. BOR은 레거시 테이블 부재로 보류. =====
        // 라벨 마스터(FACTORY_STD_LABEL_MASTER) — 라벨 정의(MDM.LabelList) + 등록 폼(MDM.CreateLabel).
        Register(new ScreenDefinition("FACTORY_STD_LABEL_MASTER", "라벨 마스터",
            new FieldDefinition[]
            {
                new("labelId", "라벨 ID", Required: true), new("plantId", "공장", Required: true),
                new("labelName", "라벨명", Required: true), new("description", "설명"),
            },
            new GridColumnDefinition[]
            {
                new("LABEL_ID", "라벨 ID"), new("PLANT_ID", "공장"), new("LABEL_NAME", "라벨명"),
                new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "MDM.LabelList",
            SaveQueryId: "MDM.CreateLabel", DeleteQueryId: "MDM.DeleteLabel", Purpose: ScreenPurpose.Manage));

        // 라벨 발행 이력(FACTORY_STD_LABEL_ISSUE_HISTORY) — 발행 이력(MDM.LabelIssueList).
        Register(new ScreenDefinition("FACTORY_STD_LABEL_ISSUE_HISTORY", "라벨 발행 이력",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ISSUE_ID", "발행 ID"), new("PLANT_ID", "공장"), new("LABEL_ID", "라벨"), new("ITEM_ID", "품목"),
                new("LOT_ID", "LOT"), new("SERIAL_NUM", "시리얼"), new("PRINT_CNT", "출력수"), new("ISSUED_AT", "발행시각"),
            },
            QueryId: "MDM.LabelIssueList", Purpose: ScreenPurpose.Inquiry));

        // 라벨 매핑 관리(FACTORY_STD_LABEL_MAPPING_MANAGEMENT) — 공정/품목↔라벨 매핑(MDM.LabelMappingList) + 등록 폼.
        Register(new ScreenDefinition("FACTORY_STD_LABEL_MAPPING_MANAGEMENT", "라벨 매핑 관리",
            new FieldDefinition[]
            {
                new("mappingId", "매핑 ID", Required: true), new("plantId", "공장", Required: true),
                new("processId", "공정"), new("itemId", "품목"), new("labelId", "라벨", Required: true),
                new("printLimitCnt", "출력한도", FieldType.Number),
            },
            new GridColumnDefinition[]
            {
                new("MAPPING_ID", "매핑 ID"), new("PLANT_ID", "공장"), new("PROCESS_ID", "공정"), new("ITEM_ID", "품목"),
                new("LABEL_ID", "라벨"), new("PRINT_LIMIT_CNT", "출력한도"), new("PRINT_LIMIT_YN", "한도적용"),
            },
            QueryId: "MDM.LabelMappingList",
            SaveQueryId: "MDM.CreateLabelMapping", DeleteQueryId: "MDM.DeleteLabelMapping", Purpose: ScreenPurpose.Manage));

        // ===== SmartUX EPT_STD(설비성능 표준) 점등 — 레거시 EPT_TB_LAYOUT/EQUIPMENT_EPT_PROPERTY를 V055(EST_EPT_*)로 포팅. =====
        // 레이아웃 관리(EPT_STD_LAYOUT_MGNT)·레이아웃 구성(EPT_STD_LAYOUT_EDIT) — 레이아웃 마스터(EST.LayoutList).
        // (구성=에디터 UI지만 우선 레이아웃 목록 렌더로 점등; 실 편집기는 후속.)
        foreach (var (uiId, title) in new[] {
            ("EPT_STD_LAYOUT_MANAGEMENT", "레이아웃 관리"),
            ("EPT_STD_LAYOUT_EDIT", "레이아웃 구성") })
            Register(new ScreenDefinition(uiId, title,
                Array.Empty<FieldDefinition>(),
                new GridColumnDefinition[]
                {
                    new("LAYOUT_ID", "레이아웃 ID"), new("PLANT_ID", "공장"), new("LAYOUT_NAME", "레이아웃명"),
                    new("AREA_ID", "구역"), new("WIDTH", "폭"), new("HEIGHT", "높이"), new("IMAGE_URL", "이미지"), new("IS_ACTIVE", "활성"),
                },
                QueryId: "EST.LayoutList"));

        // 설비 EPT 속성 관리(EPT_STD_EQUIPMENT_PROPERTY) — 설비별 EPT 속성(EST.EquipmentPropertyList).
        Register(new ScreenDefinition("EPT_STD_EQUIPMENT_PROPERTY", "설비 EPT 속성 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("PLANT_ID", "공장"), new("DESCRIPTION", "설명"), new("CYCLE_TIME", "사이클타임"),
                new("DO_ALARM_INTERLOCK", "알람인터락"), new("DO_MCC", "MCC"), new("DO_SUMMARY", "요약"),
                new("DO_TACT_TIME", "택트타임"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "EST.EquipmentPropertyList"));

        // ===== SmartUX MICUBE(설비상태 표준) → EST 이관 점등(브랜드명 MICUBE는 백엔드 미사용, 메뉴 UI_ID만 고정). =====
        // 설비 상태 정보(MICUBE_STANDARD_EQUIPMENT_STATE) — 현재 상태(EST.CurrentStateList 재사용).
        Register(new ScreenDefinition("MICUBE_STANDARD_EQUIPMENT_STATE", "설비 상태 정보",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비 ID"), new("PLANT_ID", "공장"), new("CURRENT_STATE_ID", "현재 상태"),
                new("STATE_CHANGED_AT", "상태변경시각"), new("STATE_VERSION", "버전"),
            },
            QueryId: "EST.CurrentStateList"));

        // 설비 상태 매트릭스(MICUBE_STANDARD_EQUIPMENT_STATE_MATRIX) — 상태 전이 매트릭스(EST.StateMatrixList).
        Register(new ScreenDefinition("MICUBE_STANDARD_EQUIPMENT_STATE_MATRIX", "설비 상태 매트릭스",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PLANT_ID", "공장"), new("FROM_STATE_ID", "이전 상태"), new("TO_STATE_ID", "변경 상태"),
                new("ALLOW_FLAG", "허용"), new("SET_STATE_ID", "설정 상태"), new("REQUIRE_REASON", "사유필수"), new("VALID_STATE", "유효"),
            },
            QueryId: "EST.StateMatrixList"));

        // 설비 이벤트 관리(MICUBE_STANDARD_EQUIPMENT_EVENT) — 설비 이벤트 마스터(EST.EquipmentEventList) + 등록 폼.
        Register(new ScreenDefinition("MICUBE_STANDARD_EQUIPMENT_EVENT", "설비 이벤트 관리",
            new FieldDefinition[]
            {
                new("eventId", "이벤트 ID", Required: true), new("plantId", "공장", Required: true),
                new("eventName", "이벤트명", Required: true), new("equipmentId", "설비"), new("eventType", "유형"),
            },
            new GridColumnDefinition[]
            {
                new("EVENT_ID", "이벤트 ID"), new("PLANT_ID", "공장"), new("EVENT_NAME", "이벤트명"),
                new("EQUIPMENT_ID", "설비"), new("EVENT_TYPE", "유형"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "EST.EquipmentEventList",
            SaveQueryId: "EST.CreateEquipmentEvent", DeleteQueryId: "EST.DeleteEquipmentEvent", Purpose: ScreenPurpose.Manage));

        // 설비 알람-상태 매핑(MICUBE_STANDARD_EQUIPMENT_STATE_ALARM_MAPPING) — 알람→상태(EST.StateAlarmMapList) + 등록 폼.
        Register(new ScreenDefinition("MICUBE_STANDARD_EQUIPMENT_STATE_ALARM_MAPPING", "설비 알람-상태 매핑",
            new FieldDefinition[]
            {
                new("mapId", "매핑 ID", Required: true), new("plantId", "공장", Required: true),
                new("equipmentId", "설비", Required: true), new("alarmDefId", "알람정의", Required: true),
                new("setState", "설정 상태"),
            },
            new GridColumnDefinition[]
            {
                new("MAP_ID", "매핑 ID"), new("PLANT_ID", "공장"), new("EQUIPMENT_ID", "설비"),
                new("ALARM_DEF_ID", "알람정의"), new("SET_STATE", "설정 상태"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "EST.StateAlarmMapList",
            SaveQueryId: "EST.CreateStateAlarmMap", DeleteQueryId: "EST.DeleteStateAlarmMap", Purpose: ScreenPurpose.Manage));

        // 설비 이벤트-상태 매핑(MICUBE_STANDARD_EQUIPMENT_STATE_EVENT_MAPPING) — 이벤트→상태(EST.StateEventMapList) + 등록 폼.
        Register(new ScreenDefinition("MICUBE_STANDARD_EQUIPMENT_STATE_EVENT_MAPPING", "설비 이벤트-상태 매핑",
            new FieldDefinition[]
            {
                new("mapId", "매핑 ID", Required: true), new("plantId", "공장", Required: true),
                new("equipmentId", "설비", Required: true), new("eventId", "이벤트", Required: true),
                new("setState", "설정 상태"),
            },
            new GridColumnDefinition[]
            {
                new("MAP_ID", "매핑 ID"), new("PLANT_ID", "공장"), new("EQUIPMENT_ID", "설비"),
                new("EVENT_ID", "이벤트"), new("SET_STATE", "설정 상태"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "EST.StateEventMapList",
            SaveQueryId: "EST.CreateStateEventMap", DeleteQueryId: "EST.DeleteStateEventMap", Purpose: ScreenPurpose.Manage));

        // ===== SmartUX MICUBE(알람메일 알림) → COM 이관 점등. 메일서버/수신자매핑/서비스(V057, COM_ 접두사). =====
        // 메일 서버 관리(MICUBE_STANDARD_MAIL_SERVER) — 메일 서버(COM.MailServerList).
        Register(new ScreenDefinition("MICUBE_STANDARD_MAIL_SERVER", "메일 서버 관리",
            new FieldDefinition[]
            {
                new("serverId", "서버 ID", Required: true), new("serverName", "서버명", Required: true),
                new("host", "호스트"), new("port", "포트", FieldType.Number),
                new("senderAddress", "발신주소"),
                new("useSsl", "SSL", FieldType.Select, Options: new[] { "Y", "N" }),
            },
            new GridColumnDefinition[]
            {
                new("SERVER_ID", "서버 ID"), new("SERVER_NAME", "서버명"), new("HOST", "호스트"), new("PORT", "포트"),
                new("SENDER_ADDRESS", "발신주소"), new("USE_SSL", "SSL"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "COM.MailServerList",
            SaveQueryId: "COM.CreateMailServer", DeleteQueryId: "COM.DeleteMailServer", Purpose: ScreenPurpose.Manage));

        // 사용자-설비 메일 매핑(일반=MailRecipientList / 알람=AlarmMailRecipientList). 수신자 그리드 공용.
        // 알람메일 매핑 화면에는 등록 폼(COM.CreateMailRecipient, mailType Select)을 함께 둔다.
        var mailRecipientCols = new GridColumnDefinition[]
        {
            new("RECIPIENT_ID", "수신 ID"), new("PLANT_ID", "공장"), new("USER_ID", "사용자"), new("EQUIPMENT_ID", "설비"),
            new("MAIL_ADDRESS", "메일주소"), new("MAIL_TYPE", "유형"), new("IS_ACTIVE", "활성"),
        };
        Register(new ScreenDefinition("MICUBE_STANDARD_USER_EQUIPMENT_ALARM_MAIL_MAP", "사용자-설비 알람메일 매핑",
            new FieldDefinition[]
            {
                new("recipientId", "수신 ID", Required: true), new("plantId", "공장", Required: true),
                new("userId", "사용자", Required: true), new("equipmentId", "설비"), new("mailAddress", "메일주소"),
                new("mailType", "유형", FieldType.Select, Options: new[] { "Alarm", "Mail" }),
            },
            mailRecipientCols, QueryId: "COM.AlarmMailRecipientList", SaveQueryId: "COM.CreateMailRecipient", DeleteQueryId: "COM.DeleteMailRecipient",
            Purpose: ScreenPurpose.Manage));
        foreach (var (uiId, title, queryId) in new[] {
            ("MICUBE_STANDARD_STD_USER_ALARM_MAILING", "알람 메일 수신자 관리", "COM.AlarmMailRecipientList"),
            ("MICUBE_STANDARD_USER_EQUIPMENT_MAIL_MAP", "사용자-설비 메일 매핑", "COM.MailRecipientList"),
            ("MICUBE_STANDARD_STD_EQUIPMENT_MAILING", "설비 메일링 관리", "COM.MailRecipientList") })
            Register(new ScreenDefinition(uiId, title,
                Array.Empty<FieldDefinition>(), mailRecipientCols, QueryId: queryId));

        // 서비스 관리(MICUBE_STANDARD_SERVICE_MANAGEMENT) — 서비스 목록(COM.ServiceList).
        Register(new ScreenDefinition("MICUBE_STANDARD_SERVICE_MANAGEMENT", "서비스 정의",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("SERVICE_ID", "서비스 ID"), new("SERVICE_NAME", "서비스명"), new("SERVICE_TYPE", "유형"),
                new("STATUS", "상태"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "COM.ServiceList"));

        // ===== SmartUX 잔여 보류분 점등 — STD BOR(V058 자원명세) + PRC 이동오더(IVT.MoveList 재사용). =====
        // BOR 관리 조건 기준(FACTORY_STD_BOR_CONDITION) — BOR 헤더(MDM.BorList) + 등록 폼(MDM.CreateBor).
        Register(new ScreenDefinition("FACTORY_STD_BOR_CONDITION", "BOR 관리(조건 기준)",
            new FieldDefinition[]
            {
                new("borId", "BOR ID", Required: true), new("plantId", "공장", Required: true),
                new("borName", "BOR명", Required: true),
                new("borType", "유형", FieldType.Select, Options: new[] { "Condition", "Resource" }),
                new("processId", "공정"), new("productId", "품목"),
            },
            new GridColumnDefinition[]
            {
                new("BOR_ID", "BOR ID"), new("PLANT_ID", "공장"), new("BOR_NAME", "BOR명"), new("PROCESS_ID", "공정"),
                new("PRODUCT_ID", "품목"), new("BOR_TYPE", "유형"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "MDM.BorList",
            SaveQueryId: "MDM.CreateBor", DeleteQueryId: "MDM.DeleteBor", Purpose: ScreenPurpose.Manage));

        // BOR 관리 자원 기준(FACTORY_STD_BOR_RESOURCE) — BOR 자원 상세(MDM.BorResourceList).
        Register(new ScreenDefinition("FACTORY_STD_BOR_RESOURCE", "BOR 관리(자원 기준)",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("RESOURCE_ID", "자원 ID"), new("BOR_ID", "BOR"), new("RESOURCE_TYPE", "자원유형"),
                new("RESOURCE_REF_ID", "참조 ID"), new("RESOURCE_NAME", "자원명"), new("REQUIRED_QTY", "소요량"),
                new("CONDITION_VALUE", "조건값"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "MDM.BorResourceList"));

        // 이동오더 현황(FACTORY_PRC_REPORT_MOVEORDER) — 자재 이동 트랜잭션 재사용(IVT.MoveList).
        Register(new ScreenDefinition("FACTORY_PRC_REPORT_MOVEORDER", "이동오더 현황",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("TX_ID", "이동 ID"), new("LOT_ID", "LOT"), new("MATERIAL_ID", "자재"), new("QTY", "수량"),
                new("FROM_WAREHOUSE", "출발창고"), new("TO_WAREHOUSE", "도착창고"), new("TX_AT", "이동시각"),
                new("PROCESSED_BY", "처리자"), new("STATUS", "상태"),
            },
            QueryId: "IVT.MoveList", Purpose: ScreenPurpose.Report));

        // ===== SmartUX 잔여(c 블록) 재사용 점등 — POC/WPM 자재를 기존 쿼리로(마이그레이션 0). =====
        // POC 진행중 LOT/공정 진행(코팅·믹싱·롤투롤) — Lot 마스터(POM.LotList) 재사용.
        var pocLotCols = new GridColumnDefinition[]
        {
            new("LOT_ID", "LOT"), new("PLANT_ID", "공장"), new("PRODUCT_ID", "품목"), new("QTY", "수량"),
            new("LOT_STATE", "LOT상태"), new("PROCESS_STATE", "공정상태"), new("CURRENT_STEP", "스텝"), new("EQUIPMENT_ID", "설비"), new("IS_HOLD", "홀드"),
        };
        Register(new ScreenDefinition("POC_INPROCESS_LOT", "진행중 LOT 현황",
            Array.Empty<FieldDefinition>(), pocLotCols, QueryId: "POM.LotList", Purpose: ScreenPurpose.Report));
        foreach (var (uiId, title) in new[] {
            ("POC_COATING_PROCESS", "코팅 공정 진행"),
            ("POC_MIXING_PROCESS", "믹싱 공정 진행"), ("POC_ROLLING_PROCESS", "롤투롤 공정 진행") })
            Register(new ScreenDefinition(uiId, title, Array.Empty<FieldDefinition>(), pocLotCols,
                QueryId: "POM.LotList", Purpose: ScreenPurpose.Report));

        // POC 생산 LOT 추적(목록/트리) — Lot 이력(POM.LotTraceList) 재사용.
        var pocTraceCols = new GridColumnDefinition[]
        {
            new("LOT_ID", "LOT"), new("PLANT_ID", "공장"), new("EQUIPMENT_ID", "설비"), new("PROCESS_ID", "공정"),
            new("TRACK_IN_TIME", "In시각"), new("TRACK_OUT_TIME", "Out시각"), new("EXECUTION_ID", "실행"), new("QTY", "수량"), new("LOT_STATE", "LOT상태"),
        };
        foreach (var (uiId, title) in new[] { ("POC_LOT_TRACE", "생산 LOT 추적"), ("POC_LOT_TRACE_TREE", "생산 LOT 추적(트리)") })
            Register(new ScreenDefinition(uiId, title, Array.Empty<FieldDefinition>(), pocTraceCols,
                QueryId: "POM.LotTraceList", Purpose: ScreenPurpose.Inquiry));

        // POC FDC 데이터 차트 — 수집 시계열(FDC.CollectDataList) 재사용.
        Register(new ScreenDefinition("POC_FDC_DATA_CHART", "FDC 데이터 차트(POC)",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("EQUIPMENT_ID", "설비"), new("PARAMETER_ID", "파라미터"), new("VALUE", "측정값"),
                new("COLLECTED_AT", "수집시각"), new("QUALITY", "품질"), new("LOWER_LIMIT", "하한"), new("UPPER_LIMIT", "상한"),
            },
            QueryId: "FDC.CollectDataList", Purpose: ScreenPurpose.Report));

        var pomWoFields = new FieldDefinition[]
        {
            new("workOrderId", "작업지시 ID", Required: true),
            new("productionOrderId", "생산관리오더 ID", Required: true),
            new("plantId", "공장 ID", Required: true),
            new("workOrderName", "작업지시명", Required: true),
            new("productId", "품목 ID", Required: true),
            new("planQty", "계획 수량", FieldType.Number, Required: true),
            new("routingScope", "라우팅 실행 범위", FieldType.Select, Required: true,
                Options: new[] { "Unbound", "Operation", "SerialRoute" }),
            new("routingId", "제품 라우팅 ID"),
            new("routingStepNo", "공정 순번", FieldType.Number),
            new("processId", "공정 ID"),
            new("workCenterId", "워크센터 ID"),
            new("areaId", "구역 ID"),
            new("equipmentId", "설비 ID"),
            new("ownerId", "담당자 ID"),
            new("planStartDate", "계획 시작일", FieldType.Date),
            new("planEndDate", "계획 종료일", FieldType.Date),
            new("workOrderType", "작업지시 유형"),
            new("salesOrderId", "수주 번호"),
            new("description", "설명"),
        };
        var pomWoCols = new GridColumnDefinition[]
        {
            new("WORK_ORDER_ID", "W/O ID"), new("WORK_ORDER_NAME", "W/O명"), new("PLANT_ID", "공장"),
            new("PRODUCTION_ORDER_ID", "생산관리오더"), new("ROUTING_SCOPE", "라우팅 실행 범위"),
            new("ROUTING_ID", "제품 라우팅"), new("ROUTING_STEP_NO", "공정 순번"), new("PROCESS_ID", "공정"),
            new("WORK_CENTER_ID", "워크센터"), new("EQUIPMENT_ID", "설비"), new("OWNER_ID", "작업자"),
            new("PRODUCT_ID", "품목"), new("PLAN_QTY", "계획수량"), new("START_QTY", "착수수량"),
            new("COMPLETE_QTY", "완료수량"), new("SCRAP_QTY", "불량수량"), new("STATUS", "상태"), new("IS_HOLD", "홀드"),
            new("VERSION_NO", "버전"),
        };

        // POC 작업지시 관리 — 공정 단위(Operation)와 단일 W/O 전체 라우팅(SerialRoute)을 함께 조회한다.
        Register(new ScreenDefinition("POC_PPM_WORK_ORDER", "작업지시 관리",
            pomWoFields,
            pomWoCols,
            QueryId: "POM.WorkOrderList", SaveQueryId: PomWorkOrderMetaCommands.Create,
            Purpose: ScreenPurpose.Manage, SaveRequiredPermission: "pom:manage"));

        // WPM 투입 자재/불출 현황 — 자재 트랜잭션(IVT.MaterialTxList/DispensingList) 재사용.
        var wpmMaterialTxCols = new GridColumnDefinition[]
        {
            new("TX_ID", "트랜잭션 ID"), new("LOT_ID", "LOT"), new("MATERIAL_ID", "자재"), new("TX_TYPE", "유형"), new("QTY", "수량"),
            new("FROM_WAREHOUSE", "출발창고"), new("TO_WAREHOUSE", "도착창고"), new("TX_AT", "시각"), new("PROCESSED_BY", "처리자"), new("STATUS", "상태"),
        };
        Register(new ScreenDefinition("FACTORY_WPM_REPORT_CONSUME_MATERIAL_LOT", "투입 자재 현황",
            Array.Empty<FieldDefinition>(), wpmMaterialTxCols, QueryId: "IVT.MaterialTxList", Purpose: ScreenPurpose.Report));
        Register(new ScreenDefinition("FACTORY_WPM_REPORT_MATERIAL_DISPENSING_ORDER", "자재 불출 현황",
            Array.Empty<FieldDefinition>(), wpmMaterialTxCols, QueryId: "IVT.DispensingList", Purpose: ScreenPurpose.Report));

        // ===== SmartUX 잔여(c 블록) 레거시 포팅 점등 — 벤더(V059)·작업지시(V060)·COM 액션(V061)·파일(V012 재사용). =====

        // 협력사 관리(MES_MDM_COM_VENDOR)·협력사 품목 관리(MES_MDM_COM_VENDOR_ITEM) — V059(MDM.Vendor*List).
        Register(new ScreenDefinition("MES_MDM_COM_VENDOR", "협력사 관리",
            new FieldDefinition[]
            {
                new("vendorId", "벤더 ID", Required: true), new("vendorName", "벤더명", Required: true),
                new("vendorType", "유형"), new("phone", "전화"), new("email", "이메일"),
            },
            new GridColumnDefinition[]
            {
                new("VENDOR_ID", "벤더 ID"), new("VENDOR_NAME", "벤더명"), new("VENDOR_TYPE", "유형"),
                new("CORPORATION_NO", "사업자번호"), new("OWNER_NAME", "대표자"), new("PHONE", "전화"),
                new("EMAIL", "이메일"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "MDM.VendorList",
            SaveQueryId: "MDM.CreateVendor", DeleteQueryId: "MDM.DeleteVendor", Purpose: ScreenPurpose.Manage));
        Register(new ScreenDefinition("MES_MDM_COM_VENDOR_ITEM", "협력사 품목 관리",
            new FieldDefinition[]
            {
                new("vendorItemId", "매핑 ID", Required: true), new("vendorId", "벤더", Required: true),
                new("productId", "품목", Required: true), new("leadTimeDays", "리드타임(일)", FieldType.Number),
                new("moq", "최소발주량", FieldType.Number), new("basePrice", "기준단가", FieldType.Number),
            },
            new GridColumnDefinition[]
            {
                new("VENDOR_ITEM_ID", "매핑 ID"), new("VENDOR_ID", "벤더"), new("PRODUCT_ID", "품목"),
                new("LEAD_TIME_DAYS", "리드타임(일)"), new("MOQ", "최소발주량"), new("BASE_PRICE", "기준단가"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "MDM.VendorItemList",
            SaveQueryId: "MDM.CreateVendorItem", DeleteQueryId: "MDM.DeleteVendorItem", Purpose: ScreenPurpose.Manage));

        // 작업 관리 — 이 설비는 생산 작업지시를 실행 단위로 사용하지 않는다.
        // Campaign→Batch→Carrier 계층을 POM 작업 범위로 관리하고, Carrier는 LOT 없이 Carrier ID로
        // 추적한다. 기존 POC_PPM_WORK_ORDER와 생산지시 참조 그리드는 하위 호환/외부 연계용으로 유지한다.
        Register(new ScreenDefinition("FACTORY_PPM_WORK_ORDER", "작업 관리",
            Array.Empty<FieldDefinition>(),
            Layout: BuildEquipmentWorkManagementLayout(),
            SearchFields: BuildEquipmentWorkManagementSearchFields(),
            BulkCommands: BuildEquipmentWorkScopeBulkCommands(),
            Purpose: ScreenPurpose.Manage));
        Register(new ScreenDefinition("FACTORY_PPM_REPORT_WORKORDER", "작업지시 현황",
            Array.Empty<FieldDefinition>(), pomWoCols, QueryId: "POM.WorkOrderList", Purpose: ScreenPurpose.Report));

        // 알람 액션 관리(FACTORY_COM_ACTION_DEF)·알람별 액션 관리(FACTORY_COM_ALARM_ACTION) — V061(COM.ActionList/AlarmActionList).
        Register(new ScreenDefinition("FACTORY_COM_ACTION_DEF", "알람 액션 관리",
            new FieldDefinition[]
            {
                new("actionId", "액션 ID", Required: true), new("actionName", "액션명", Required: true),
                new("actionType", "유형", FieldType.Select, Options: new[] { "Hold", "Email", "Sms", "Procedure" }),
                new("description", "설명"),
            },
            new GridColumnDefinition[]
            {
                new("ACTION_ID", "액션 ID"), new("ACTION_NAME", "액션명"), new("ACTION_TYPE", "유형"),
                new("HOLD_CODE", "홀드코드"), new("EMAIL_TITLE", "메일제목"), new("SMS_TITLE", "SMS제목"),
                new("PROCEDURE_NAME", "프로시저"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "COM.ActionList",
            SaveQueryId: "COM.CreateAction", DeleteQueryId: "COM.DeleteAction", Purpose: ScreenPurpose.Manage));
        Register(new ScreenDefinition("FACTORY_COM_ALARM_ACTION", "알람별 액션 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ALARM_ACTION_ID", "매핑 ID"), new("ALARM_ID", "알람"), new("ACTION_ID", "액션"),
                new("ACTION_SEQUENCE", "순서"), new("DESCRIPTION", "설명"), new("IS_ACTIVE", "활성"),
            },
            QueryId: "COM.AlarmActionList"));

        // 파일 관리(SYSTEM_2_FILE_MENU) — 배포 파일 메타 재사용(V012 SYS_DEPLOY_FILE, SYS.DeployFileList).
        Register(new ScreenDefinition("SYSTEM_2_FILE_MENU", "파일 관리",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("FILE_ID", "파일 ID"), new("VERSION", "버전"), new("FILE_NAME", "파일명"), new("FILE_SIZE", "크기"),
                new("DESCRIPTION", "설명"), new("FORCE_UPDATE", "강제업데이트"), new("IS_ACTIVE", "활성"),
                new("UPLOADED_BY", "업로더"), new("UPLOADED_AT", "업로드시각"),
            },
            QueryId: "SYS.DeployFileList"));

        // 로그 뷰어(LOG_VIEWER) — 앱 로그(V064, DbLoggerProvider Warning+ 기록·SYS.AppLogList).
        // SearchFields 1호 적용 — @logLevel NULL-가드 필터(빈 값=전체). 서버측 페이징 1호(P3-9 v2, CountQueryId).
        Register(new ScreenDefinition("LOG_VIEWER", "로그 뷰어",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("LOGGED_AT", "발생시각"), new("LOG_LEVEL", "레벨"), new("CATEGORY", "카테고리"),
                new("MESSAGE", "메시지"), new("EXCEPTION", "예외"),
            },
            QueryId: "SYS.AppLogList",
            SearchFields: new FieldDefinition[]
            {
                new("logLevel", "레벨", FieldType.Select, Options: new[] { "Information", "Warning", "Error", "Critical" }),
            },
            CountQueryId: "SYS.AppLogListCount", Purpose: ScreenPurpose.Inquiry));

        // 요청 로그 뷰어(SYSTEM2_MONITOR_REQLOG) — API 요청 로그(V062, RequestLogMiddleware 기록·SYS.RequestLogList).
        Register(new ScreenDefinition("SYSTEM2_MONITOR_REQLOG", "요청 로그 뷰어",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("REQUESTED_AT", "요청시각"), new("METHOD", "메서드"), new("PATH", "경로"), new("STATUS_CODE", "상태"),
                new("ELAPSED_MS", "소요(ms)"), new("USER_ID", "사용자"), new("CLIENT_IP", "클라이언트 IP"),
            },
            QueryId: "SYS.RequestLogList",
            SearchFields: new FieldDefinition[]
            {
                new("method", "메서드", FieldType.Select, Options: new[] { "GET", "POST", "PUT", "DELETE" }),
                new("userId", "사용자 ID"),
            },
            CountQueryId: "SYS.RequestLogListCount", Purpose: ScreenPurpose.Inquiry));

        // 생산성 대시보드(FACTORY_DASHBOARD_MENU_PRODUCTIVITY) — 설비×일자 OEE 마트(EST.OeeSummaryList) 재사용.
        Register(new ScreenDefinition("FACTORY_DASHBOARD_MENU_PRODUCTIVITY", "생산성 대시보드",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("OEE_DATE", "일자"), new("EQUIPMENT_ID", "설비"), new("AVAILABILITY_PERCENT", "가동률 (%)"),
                new("PERFORMANCE_PERCENT", "성능가동률 (%)"), new("QUALITY_PERCENT", "양품률 (%)"), new("OEE_PERCENT", "OEE (%)"),
                new("TOTAL_COUNT", "총생산"), new("GOOD_COUNT", "양품"),
            },
            QueryId: "EST.OeeSummaryList", Purpose: ScreenPurpose.Report));

        // 대시보드 샘플 화면(FACTORY_DASHBOARD_MENU_SAMPLE_TEST) — 품목별 수율 집계(POM.YieldByProduct) 샘플 바인딩.
        Register(new ScreenDefinition("FACTORY_DASHBOARD_MENU_SAMPLE_TEST", "대시보드 샘플",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("PRODUCT_ID", "품목"), new("LOT_COUNT", "LOT수"), new("TOTAL_QTY", "총생산"),
                new("DEFECT_QTY", "불량"), new("GOOD_QTY", "양품"),
            },
            QueryId: "POM.YieldByProduct", Purpose: ScreenPurpose.Report));

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
            QueryId: "SHP.DeliveryOrderList", Purpose: ScreenPurpose.Report));

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
            QueryId: "QMS.DefectList", Purpose: ScreenPurpose.Report));

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
        Register(new ScreenDefinition("QMS_STD_INSP_DEF", "검사 정의 관리",
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
        Register(new ScreenDefinition("QMS_GAUGE_MEASURE_EQUIPMENT_MANAGEMENT", "계측기 마스터 관리",
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
            QueryId: "QMS.SpmEvalResultList", Purpose: ScreenPurpose.Inquiry));

        // 시정 조치 결과 등록(QMS_SPM_ADMIN_ACTION_RESULT_REGIST) — 협력사 시정 조치 이력(QMS.SpmActionResultList).
        Register(new ScreenDefinition("QMS_SPM_ADMIN_ACTION_RESULT_REGISTRATION", "시정 조치 결과 등록",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("ACTION_ID", "조치 ID"), new("RESULT_ID", "실적 ID"), new("SUPPLIER_ID", "협력사 ID"),
                new("ACTION_DESC", "조치내용"), new("ACTION_DATE", "조치일"), new("STATUS", "상태"), new("COMPLETED_AT", "완료일"),
            },
            QueryId: "QMS.SpmActionResultList"));

        // ===== SmartUX QMS 검사(수입/공정/출하) 점등(V040 신설 QMS_INSPECTION) — 등록/이력/현황을 타입별 쿼리로 바인딩. =====
        RegisterQcaInspection("QMS_INSP_IMPORT_INSPECTION", "수입 검사 등록", "QMS.IncomingInspectionList", QmsInspectionMetaCommands.RecordIncoming);
        RegisterQcaInspection("QMS_INSP_IMPORT_REGISTRATION_HIST", "수입 검사 이력 조회", "QMS.IncomingInspectionList", purpose: ScreenPurpose.Inquiry);
        RegisterQcaInspection("QMS_REP_IMPORT_STATUS", "수입 검사 현황", "QMS.IncomingInspectionList", purpose: ScreenPurpose.Report);
        RegisterQcaInspection("QMS_INSP_PROCESS_INSPECTION", "공정 검사 등록", "QMS.ProcessInspectionList", QmsInspectionMetaCommands.RecordProcess);
        RegisterQcaInspection("QMS_INSP_PROCESS_INSPECTION_LOT", "공정 검사 등록 (LOT)", "QMS.ProcessInspectionList", QmsInspectionMetaCommands.RecordProcess);
        RegisterQcaInspection("QMS_INSP_PROCESS_REGISTRATION_HIST", "공정 검사 이력 조회", "QMS.ProcessInspectionList", purpose: ScreenPurpose.Inquiry);
        RegisterQcaInspection("QMS_REP_PROCESS_STATUS", "공정 검사 현황", "QMS.ProcessInspectionList", purpose: ScreenPurpose.Report);
        RegisterQcaInspection("QMS_INSP_SHIPPING_INSPECTION", "출하 검사 등록", "QMS.ShippingInspectionList", QmsInspectionMetaCommands.RecordShipping);
        RegisterQcaInspection("QMS_INSP_SHIPPING_REGISTRATION_HIST", "출하 검사 이력 조회", "QMS.ShippingInspectionList", purpose: ScreenPurpose.Inquiry);
        RegisterQcaInspection("QMS_REP_SHIPPING_STATUS", "출하 검사 현황", "QMS.ShippingInspectionList", purpose: ScreenPurpose.Report);

        // ===== SmartUX QMS 장기재고검사(자재/제품) 점등(V041 신설 QMS_LONGTERM_INSPECTION) — 의뢰/결과/이력을 대상별 쿼리로. =====
        var ltInspCols = new GridColumnDefinition[]
        {
            new("LT_INSP_ID", "검사 ID"), new("TARGET_TYPE", "대상"), new("PRODUCT_ID", "품목 ID"), new("LOT_ID", "LOT ID"),
            new("WAREHOUSE", "창고"), new("REQUEST_DATE", "의뢰일"), new("INSPECTED_AT", "검사일시"),
            new("RESULT", "결과"), new("STATUS", "상태"),
        };
        Register(new ScreenDefinition("QMS_INSP_LONGTERM_REQUEST", "자재 장기재고 검사 의뢰 현황", Array.Empty<FieldDefinition>(), ltInspCols,
            QueryId: "QMS.MaterialLongtermInspectionList", Purpose: ScreenPurpose.Report));
        Register(new ScreenDefinition("QMS_LONGTERM_INSP_RESULT", "자재 장기재고 검사 결과 등록", Array.Empty<FieldDefinition>(), ltInspCols, QueryId: "QMS.MaterialLongtermInspectionList"));
        Register(new ScreenDefinition("QMS_INSP_LONGTERM_HISTORY", "자재 장기재고 검사 결과 이력", Array.Empty<FieldDefinition>(), ltInspCols,
            QueryId: "QMS.MaterialLongtermInspectionList", Purpose: ScreenPurpose.Inquiry));
        Register(new ScreenDefinition("QMS_INSP_LONGTERM_PRODUCT_REQUEST", "제품 장기재고 검사 의뢰 현황", Array.Empty<FieldDefinition>(), ltInspCols,
            QueryId: "QMS.ProductLongtermInspectionList", Purpose: ScreenPurpose.Report));
        Register(new ScreenDefinition("QMS_INSP_LONGTERM_PRODUCT_INSP_RESULT", "제품 장기재고 검사 결과 등록", Array.Empty<FieldDefinition>(), ltInspCols, QueryId: "QMS.ProductLongtermInspectionList"));
        Register(new ScreenDefinition("QMS_INSP_LONGTERM_PRODUCT_INSP_HISTORY", "제품 장기재고 검사 결과 이력", Array.Empty<FieldDefinition>(), ltInspCols,
            QueryId: "QMS.ProductLongtermInspectionList", Purpose: ScreenPurpose.Inquiry));

        // ===== SmartUX QMS 클레임(QMS_CLM) 점등(V042 신설 QMS_CLAIM). =====
        var claimCols = new GridColumnDefinition[]
        {
            new("CLAIM_ID", "클레임 ID"), new("CLAIM_NO", "클레임번호"), new("CUSTOMER_NAME", "고객사"),
            new("PRODUCT_ID", "품목 ID"), new("CLAIM_TYPE", "유형"), new("OCCURRED_DATE", "발생일"),
            new("SEVERITY", "심각도"), new("STATUS", "상태"),
        };
        Register(new ScreenDefinition("QMS_CLM_CLAIM_REGISTRATION", "고객사 클레임 접수", Array.Empty<FieldDefinition>(), claimCols, QueryId: "QMS.ClaimList"));
        Register(new ScreenDefinition("QMS_CLM_CLAIM_RESULT", "클레임 처리 결과 등록", Array.Empty<FieldDefinition>(), claimCols, QueryId: "QMS.ClaimList"));
        Register(new ScreenDefinition("QMS_CLM_STATUS_VIEW", "클레임 현황 조회", Array.Empty<FieldDefinition>(), claimCols,
            QueryId: "QMS.ClaimList", Purpose: ScreenPurpose.Inquiry));
        Register(new ScreenDefinition("QMS_CLM_RPT_OCCUR_STATUS", "클레임 발생 현황", Array.Empty<FieldDefinition>(), claimCols,
            QueryId: "QMS.ClaimList", Purpose: ScreenPurpose.Report));
        Register(new ScreenDefinition("QMS_CLM_REPORT_ACTION_STATUS", "클레임 처리 현황", Array.Empty<FieldDefinition>(), claimCols,
            QueryId: "QMS.ClaimList", Purpose: ScreenPurpose.Report));

        // ===== SmartUX QMS 품질보증(QCA) 점등(V043 신설) — NCR + Hold/Release. =====
        var ncrCols = new GridColumnDefinition[]
        {
            new("NCR_ID", "NCR ID"), new("NCR_NO", "NCR번호"), new("SOURCE_TYPE", "발생원"), new("LOT_ID", "LOT ID"),
            new("PRODUCT_ID", "품목 ID"), new("ISSUED_DATE", "발행일"), new("DISPOSITION", "처리"), new("STATUS", "상태"),
        };
        Register(new ScreenDefinition("QMS_QCA_NCR_ISSUE", "NCR 관리", Array.Empty<FieldDefinition>(), ncrCols, QueryId: "QMS.NcrList"));
        Register(new ScreenDefinition("QMS_QCA_NCR_OVERVIEW", "NCR 현황", Array.Empty<FieldDefinition>(), ncrCols,
            QueryId: "QMS.NcrList", Purpose: ScreenPurpose.Report));
        var holdCols = new GridColumnDefinition[]
        {
            new("HOLD_ID", "Hold ID"), new("LOT_ID", "LOT ID"), new("PRODUCT_ID", "품목 ID"), new("HOLD_TYPE", "유형"),
            new("RISK_RANGE", "Risk Range"), new("REQUESTED_BY", "요청자"), new("REQUESTED_AT", "요청일시"), new("STATUS", "상태"),
        };
        Register(new ScreenDefinition("QMS_QCA_RELEASE_HOLD_REG", "Hold/Release(Risk Range)", Array.Empty<FieldDefinition>(), holdCols, QueryId: "QMS.HoldReleaseList"));
        Register(new ScreenDefinition("QMS_QCA_PENDING_STATUS", "Hold/Release(Risk Range) 현황", Array.Empty<FieldDefinition>(), holdCols,
            QueryId: "QMS.HoldReleaseList", Purpose: ScreenPurpose.Report));

        // ===== SmartUX QMS 4M 변경 점등(V044 신설 QMS_4M_CHANGE). =====
        var fourMCols = new GridColumnDefinition[]
        {
            new("CHANGE_ID", "변경 ID"), new("CHANGE_NO", "변경번호"), new("CHANGE_TYPE", "4M 유형"),
            new("EQUIPMENT_ID", "설비 ID"), new("PRODUCT_ID", "품목 ID"), new("CHANGE_DATE", "변경일"), new("APPROVAL_STATUS", "승인상태"),
        };
        Register(new ScreenDefinition("QMS_4M_CHANGE_HISTORY", "4M 변경 이력 관리", Array.Empty<FieldDefinition>(), fourMCols, QueryId: "QMS.FourMChangeList"));
        Register(new ScreenDefinition("QMS_REP_CHANGE_STATUS", "변경점 발생 현황", Array.Empty<FieldDefinition>(), fourMCols,
            QueryId: "QMS.FourMChangeList", Purpose: ScreenPurpose.Report));

        // ===== SmartUX QMS 보고서성 잎 점등 — 신규 테이블 없이 기존 쿼리 재사용(계측기/협력사). =====
        var gaugeReportCols = new GridColumnDefinition[]
        {
            new("GAUGE_ID", "계측기 ID"), new("GAUGE_NAME", "계측기명"), new("GAUGE_TYPE", "유형"),
            new("LOCATION", "위치"), new("NEXT_CALIBRATION_AT", "차기검교정"), new("IS_ACTIVE", "활성"),
        };
        Register(new ScreenDefinition("QMS_MEASURE_INSTRUMENT_REPORT", "계측기 현황", Array.Empty<FieldDefinition>(), gaugeReportCols,
            QueryId: "QMS.GaugeList", Purpose: ScreenPurpose.Report));
        Register(new ScreenDefinition("QMS_MEQ_MEASURE_FAILURE_RATE", "계측기 측정 불량 현황", Array.Empty<FieldDefinition>(), gaugeReportCols,
            QueryId: "QMS.GaugeList", Purpose: ScreenPurpose.Report));
        Register(new ScreenDefinition("QMS_MEQ_CALIBRATION_STATUS", "계측기 검교정 현황", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("RESULT_ID", "내역 ID"), new("GAUGE_ID", "계측기 ID"), new("CALIBRATED_AT", "검교정일시"),
                new("RESULT", "결과"), new("CERTIFICATE_NO", "성적서번호"), new("NEXT_DUE_AT", "차기예정"),
            },
            QueryId: "QMS.GaugeCalibrationResultList", Purpose: ScreenPurpose.Report));
        Register(new ScreenDefinition("QMS_MEQ_MEASURE_REPAIR_DETAILS", "계측기 수리 현황", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("REPAIR_ID", "수리 ID"), new("GAUGE_ID", "계측기 ID"), new("REPAIRED_AT", "수리일시"),
                new("REPAIRED_BY", "수리자"), new("FAILURE_DESC", "고장내용"), new("REPAIR_DESC", "수리내용"),
            },
            QueryId: "QMS.GaugeRepairResultList", Purpose: ScreenPurpose.Report));
        var spmReportCols = new GridColumnDefinition[]
        {
            new("RESULT_ID", "실적 ID"), new("SUPPLIER_ID", "협력사 ID"), new("SUPPLIER_NAME", "협력사명"),
            new("EVAL_PERIOD", "평가기간"), new("TOTAL_SCORE", "총점"), new("GRADE", "등급"), new("EVALUATED_AT", "평가일시"),
        };
        Register(new ScreenDefinition("QMS_SPM_EVL_REPORT", "협력사 평가 현황", Array.Empty<FieldDefinition>(), spmReportCols,
            QueryId: "QMS.SpmEvalResultList", Purpose: ScreenPurpose.Report));
        Register(new ScreenDefinition("QMS_SPM_EVL_RESULT_COMPARISON", "협력사별 평가 결과 비교 조회", Array.Empty<FieldDefinition>(), spmReportCols,
            QueryId: "QMS.SpmEvalResultList", Purpose: ScreenPurpose.Inquiry));

        // 검사 현황(QMS_REP_ITEM_STATUS) — 전체 검사 실행 조회(QMS.InspectionList, 타입 무관). 구 SmartUX ID 'QMS_REP_ITEM_STATUS.js'는 별칭+PROGRAM_ID로 보존.
        Register(new ScreenDefinition("QMS_REP_ITEM_STATUS", "검사 현황",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("INSPECTION_ID", "검사 ID"), new("INSPECTION_TYPE", "유형"), new("LOT_ID", "LOT ID"),
                new("PRODUCT_ID", "품목 ID"), new("INSPECTED_AT", "검사일시"), new("RESULT", "결과"), new("IS_CONFIRMED", "확정"),
            },
            QueryId: "QMS.InspectionList", Purpose: ScreenPurpose.Report));

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
        Register(new ScreenDefinition("FACTORY_EMS_STD_SPARE_PART_INOUT_HISTORY", "Spare Part 입출고 이력", Array.Empty<FieldDefinition>(), spareInoutCols,
            QueryId: "EMS.SparePartInoutList", Purpose: ScreenPurpose.Inquiry));

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
        Register(new ScreenDefinition("FACTORY_EMS_PM_ORDER_RESULT_LIST", "PM 결과 조회", Array.Empty<FieldDefinition>(), pmPlanCols,
            QueryId: "EMS.MaintenancePlanList", Purpose: ScreenPurpose.Inquiry));

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
        // SYSTEM_2_AUTH_MANAGEMENT는 호스트 전용 페이지(HostRoleManagement — 역할 생성·권한 추가/회수·잠금 해제,
        // 브리지 REST 경유)가 리터럴 라우트로 대체한다(구 읽기 전용 목록 등록 제거).
        Register(new ScreenDefinition("SYSTEM_2_AUTH_MANAGEMENT_NEW", "권한 그룹 관리", Array.Empty<FieldDefinition>(), roleCols, QueryId: "SYS.ListRoles"));
        // SYSTEM_2_MENU_AUTH_MANAGEMENT는 상단의 SYS_MENU_ROLE 매핑 CRUD 정의를 쓴다(구 읽기 전용 메뉴 목록 대체).
        Register(new ScreenDefinition("SYSTEM_2_UIID_MANAGEMENT", "UIID 관리", Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[] { new("UI_ID", "UI ID"), new("TITLE", "제목") }, QueryId: "SYS.ListScreenDefinitions"));
        Register(new ScreenDefinition("SYSTEM_2_CODE_MANAGEMENT", "시스템 코드 관리", Array.Empty<FieldDefinition>(), stdCodeCols, QueryId: "MDM.CodeList"));

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

        // 메뉴 사용 통계(V086) — 트리 재배열(운영 메뉴 중심)의 판단 근거 화면. 전역 누적(read 전용).
        Register(new ScreenDefinition("SYS_MENU_USAGE_STATS", "메뉴 사용 통계",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("MENU_ID", "메뉴 ID", Width: 240), new("MENU_NAME", "메뉴명"),
                new("UI_ID", "화면 ID", Width: 240), new("USE_COUNT", "사용 횟수", Width: 110),
                new("LAST_USED_AT", "최근 사용"),
            },
            QueryId: "SYS.MenuUsageStats", Purpose: ScreenPurpose.Report));

        // ===== CRP v1(2026-07-10, V087) — 워크센터·라우팅 스텝 마스터 + 부하 화면. =====
        Register(new ScreenDefinition("FACTORY_STD_WORK_CENTER", "워크센터 관리",
            new FieldDefinition[]
            {
                new("workCenterId", "워크센터 ID", Required: true),
                new("workCenterName", "워크센터명", Required: true),
                new("plantId", "공장 ID"),
                new("dailyCapacityMin", "일 능력(분)", FieldType.Number),
                new("description", "설명"),
            },
            new GridColumnDefinition[]
            {
                new("WORK_CENTER_ID", "워크센터 ID", Width: 130), new("WORK_CENTER_NAME", "워크센터명"),
                new("PLANT_ID", "공장", Width: 100), new("DAILY_CAPACITY_MIN", "일 능력(분)"),
                new("IS_ACTIVE", "활성", Width: 80), new("DESCRIPTION", "설명"),
            },
            QueryId: "MDM.WorkCenterList", SaveQueryId: "MDM.CreateWorkCenter", DeleteQueryId: "MDM.DeleteWorkCenter",
            Purpose: ScreenPurpose.Manage));

        Register(new ScreenDefinition("FACTORY_STD_ROUTING_STEP", "라우팅 스텝 관리",
            new FieldDefinition[]
            {
                new("routingId", "라우팅 ID", Required: true),
                new("stepNo", "공정 순번", FieldType.Number, Required: true),
                new("stepName", "공정명"),
                new("processId", "공정 ID", Required: true),
                new("workCenterId", "워크센터 ID", Required: true),
                new("stdTimeMin", "표준시간(개당 분)", FieldType.Number),
            },
            new GridColumnDefinition[]
            {
                new("ROUTING_ID", "라우팅 ID", Width: 120), new("PRODUCT_ID", "품목", Width: 120),
                new("STEP_NO", "공정 순번", Width: 90), new("STEP_NAME", "공정명"),
                new("PROCESS_ID", "공정 ID", Width: 120),
                new("WORK_CENTER_ID", "워크센터", Width: 120), new("STD_TIME_MIN", "표준시간(분/개)"),
            },
            QueryId: "MDM.RoutingStepList", SaveQueryId: "MDM.CreateRoutingStep", DeleteQueryId: "MDM.DeleteRoutingStep",
            Purpose: ScreenPurpose.Manage));

        // 부하 조회 — 최신 MRP 런 기준 워크센터 부하/필요일수(v1 총량, 버킷별 부하는 v2).
        Register(new ScreenDefinition("NX_CRP_LOAD", "능력 소요(CRP) — 워크센터 부하",
            Array.Empty<FieldDefinition>(),
            new GridColumnDefinition[]
            {
                new("WORK_CENTER_ID", "워크센터", Width: 130), new("WORK_CENTER_NAME", "워크센터명"),
                new("DAILY_CAPACITY_MIN", "일 능력(분)"), new("LOAD_MIN", "부하(분)"),
                new("REQUIRED_DAYS", "필요 일수"),
            },
            QueryId: "POM.CrpWorkCenterLoad", Purpose: ScreenPurpose.Report));

        // ===== MRP v1 표준화(2026-07-09, V079/V080) — 단위 마스터·품목 계획 파라미터·자재 소요 계획. =====
        Register(new ScreenDefinition("FACTORY_STD_UOM", "단위(UOM) 관리",
            new FieldDefinition[]
            {
                new("uomId", "단위 ID", Required: true),
                new("uomName", "단위명", Required: true),
                new("uomType", "구분", FieldType.Select, Options: new[] { "Count", "Weight", "Volume", "Length", "Time" }),
                new("description", "설명"),
            },
            new GridColumnDefinition[]
            {
                new("UOM_ID", "단위 ID", Width: 100), new("UOM_NAME", "단위명"),
                new("UOM_TYPE", "구분", Width: 110), new("IS_ACTIVE", "활성", Width: 80), new("DESCRIPTION", "설명"),
            },
            QueryId: "MDM.UomList", SaveQueryId: "MDM.CreateUom", DeleteQueryId: "MDM.DeleteUom",
            Purpose: ScreenPurpose.Manage));

        Register(new ScreenDefinition("FACTORY_STD_ITEM_PLANNING", "품목 계획 파라미터",
            new FieldDefinition[]
            {
                new("itemId", "품목 ID", Required: true),
                new("safetyStock", "안전재고", FieldType.Number),
                new("leadTimeDays", "리드타임(일)", FieldType.Number),
                new("lotSize", "로트 크기(배수)", FieldType.Number),
                new("makeOrBuy", "조달 구분", FieldType.Select, Options: new[] { "Make", "Buy" }),
                new("description", "설명"),
            },
            new GridColumnDefinition[]
            {
                new("ITEM_ID", "품목 ID", Width: 140), new("SAFETY_STOCK", "안전재고"),
                new("LEAD_TIME_DAYS", "리드타임(일)"), new("LOT_SIZE", "로트 크기"),
                new("MAKE_OR_BUY", "조달", Width: 90), new("IS_ACTIVE", "활성", Width: 80), new("DESCRIPTION", "설명"),
            },
            QueryId: "MDM.ItemPlanningList", SaveQueryId: "MDM.CreateItemPlanning", DeleteQueryId: "MDM.DeleteItemPlanning",
            Purpose: ScreenPurpose.Manage));

        // 자재 소요 계획(MRP) — 실행은 리터럴 라우트 페이지(HostMrpPlanning)의 브리지 REST 툴바가 담당,
        // 본문(제안+실행 이력)은 이 메타 정의를 MetaScreen 자식으로 재사용한다(VE 관리 규약 1호).
        Register(new ScreenDefinition("NX_MRP_PLANNING", "자재 소요 계획(MRP)",
            Array.Empty<FieldDefinition>(),
            BulkCommands: new BulkCommandDefinition[]
            {
                // bridge: 접두 = 호스트 페이지(HostMrpPlanning) 배선 핸들러로 위임(원자 전환 — 선택 행만).
                new("선택 실오더 전환", "bridge:pom.mrp.convert", "선택한 제안(Proposed)만 실오더로 전환할까요?"),
            },
            Layout: new SectionNode
            {
                Id = "sec-mrp", Title = "자재 소요 계획(MRP v1 — 수요 주도 순소요 전개)",
                Children = new LayoutNode[]
                {
                    new RowNode { Id = "mrp-po", Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 12, Children = new LayoutNode[]
                        {
                            new TextWidget { Id = "t-po", Text = "계획오더 제안(최신 실행)", IsLabel = true },
                            new GridWidget { Id = "g-po", QueryId = "POM.MrpPlannedOrderList", Columns = new GridColumnDefinition[]
                            {
                                new("ITEM_ID", "품목", Width: 120), new("ORDER_TYPE", "유형", Width: 100),
                                new("GROSS_QTY", "총소요"), new("ON_HAND_QTY", "재고"), new("ON_ORDER_QTY", "예정입고"),
                                new("SAFETY_STOCK_QTY", "안전재고"), new("NET_QTY", "순소요"), new("SUGGESTED_QTY", "제안수량"),
                                new("RELEASE_DATE", "착수(발주)일"), new("DUE_DATE", "납기"), new("SOURCE_DEMAND", "근거 수요"),
                                new("STATUS", "상태", Width: 100), new("CONVERTED_ORDER_ID", "전환 오더", Width: 170),
                            } },
                        } },
                    } },
                    new RowNode { Id = "mrp-peg", Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 12, Children = new LayoutNode[]
                        {
                            new TextWidget { Id = "t-peg", Text = "페깅 — 수요 기여 추적(최신 실행)", IsLabel = true },
                            new GridWidget { Id = "g-peg", QueryId = "POM.MrpPeggingList", Columns = new GridColumnDefinition[]
                            {
                                new("PLANNED_ORDER_ID", "계획오더", Width: 220), new("ITEM_ID", "품목", Width: 120),
                                new("DEMAND_REF", "근거 수요"), new("QTY", "기여 수량"),
                            } },
                        } },
                    } },
                    new RowNode { Id = "mrp-run", Children = new LayoutNode[]
                    {
                        new ColumnNode { Span = 12, Children = new LayoutNode[]
                        {
                            new TextWidget { Id = "t-run", Text = "실행 이력", IsLabel = true },
                            new GridWidget { Id = "g-run", QueryId = "POM.MrpRunList", Columns = new GridColumnDefinition[]
                            {
                                new("RUN_ID", "실행 ID", Width: 220), new("STARTED_AT", "시작"), new("FINISHED_AT", "종료"),
                                new("STATUS", "상태", Width: 100), new("DEMAND_COUNT", "수요 건수"),
                                new("PLANNED_ORDER_COUNT", "제안 건수"), new("MESSAGE", "메시지"), new("EXECUTED_BY", "실행자", Width: 110),
                            } },
                        } },
                    } },
                },
            },
            Purpose: ScreenPurpose.Execute));

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
        Register(new ScreenDefinition("FACTORY_IVT_MOVE_ORDER", "자재 이동", Array.Empty<FieldDefinition>(), ivtTxCols, QueryId: "IVT.MoveList"));
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

        // canonical 코드 시드를 모두 모은 뒤 기능 계약 기반 목적 결정을 한 번만 적용한다.
        // 이후 등록하는 legacy alias는 같은 결정이 적용된 정의 인스턴스를 공유한다.
        SeedScreenPurposeDecisions.ApplyTo(_defs);

        // ===== 구 UI_ID 별칭(2026-07-09 메뉴 ID 표준화, V081) — 오타 정정 전 ID로 열리던 URL(/meta/{구ID})·
        // 잔존 즐겨찾기/최근 행이 계속 동작하게 정정된 정의를 구 ID로도 조회 가능하게 매핑한다.
        // 신규 노출(메뉴 시드·i18n·KPI 링크)은 전부 새 ID를 쓴다. 원본 대응은 시드 legacyId(PROGRAM_ID)에 보존.
        RegisterAlias("EES_EPT_OVERALL_EQUIPMENT_EFFECIVENESS", "EES_EPT_OVERALL_EQUIPMENT_EFFECTIVENESS");
        RegisterAlias("EES_POPUP_MONITERING_DASHBOARD", "EES_POPUP_MONITORING_DASHBOARD");
        RegisterAlias("FACTORY_IVT_MOVE_ODER", "FACTORY_IVT_MOVE_ORDER");
        RegisterAlias("QMS_REP_ITEM_STATUS.js", "QMS_REP_ITEM_STATUS");
        // ID 약어 정리(2026-07-10, V085) — MGNT→MANAGEMENT·REGIST→REGISTRATION·_HI→_HISTORY.
        RegisterAlias("EPT_STD_INDEX_MGNT", "EPT_STD_INDEX_MANAGEMENT");
        RegisterAlias("EPT_STD_LAYOUT_MGNT", "EPT_STD_LAYOUT_MANAGEMENT");
        RegisterAlias("QMS_GAUGE_MEASURE_EQUIPMENT_MGNT", "QMS_GAUGE_MEASURE_EQUIPMENT_MANAGEMENT");
        RegisterAlias("QMS_CLM_CLAIM_REGIST", "QMS_CLM_CLAIM_REGISTRATION");
        RegisterAlias("QMS_INSP_IMPORT_REGIST_HIST", "QMS_INSP_IMPORT_REGISTRATION_HIST");
        RegisterAlias("QMS_INSP_PROCESS_REGIST_HIST", "QMS_INSP_PROCESS_REGISTRATION_HIST");
        RegisterAlias("QMS_INSP_SHIPPING_REGIST_HIST", "QMS_INSP_SHIPPING_REGISTRATION_HIST");
        RegisterAlias("QMS_SPM_ADMIN_ACTION_RESULT_REGIST", "QMS_SPM_ADMIN_ACTION_RESULT_REGISTRATION");
        RegisterAlias("EES_FDC_VIRTUAL_EVENT_HI", "EES_FDC_VIRTUAL_EVENT_HISTORY");
    }

    public void Register(ScreenDefinition definition) => _defs[definition.UiId] = definition;

    // 구 ID 별칭 — 정의 인스턴스를 공유한다(UiId 속성은 새 ID 유지 → 제목 i18n 키도 새 키로 해석).
    private void RegisterAlias(string legacyUiId, string uiId)
    {
        if (_defs.TryGetValue(uiId, out var d)) _defs[legacyUiId] = d;
    }

    // 대시보드 KPI 카드 열(12칸 중 2칸) — 요약 쿼리(SYS.DashboardSummary) 1행의 컬럼 하나를 카드로 바인딩.
    private static ColumnNode DashKpi(string id, string label, string valueColumn, string? linkUiId = null)
        => new()
        {
            Id = $"col-{id}", Span = 2,
            Children = new LayoutNode[]
            {
                new KpiWidget { Id = id, Label = label, QueryId = "SYS.DashboardSummary", ValueColumn = valueColumn, Unit = "건", LinkUiId = linkUiId },
            },
        };

    // 수입/공정/출하 검사 화면 공통 템플릿. commandId가 있으면 헤더+반복 검사 항목을 한 번에 보내는 등록 화면,
    // 없으면 같은 이력을 읽는 조회 화면이다. 등록은 typed bridge로만 수행해 검사 유형·JWT 검사자·서버 판정·
    // 원자 저장과 멱등 처리를 우회하지 않는다. QMS/FACTORY 메뉴가 모두 이 생성기를 사용한다.
    private void RegisterQcaInspection(
        string uiId,
        string title,
        string queryId,
        string? commandId = null,
        ScreenPurpose purpose = ScreenPurpose.Auto)
    {
        var isRegistration = !string.IsNullOrWhiteSpace(commandId);
        var columns = QmsInspectionHistoryColumns();
        if (!isRegistration)
        {
            Register(new ScreenDefinition(
                uiId,
                title,
                Array.Empty<FieldDefinition>(),
                columns,
                QueryId: queryId,
                SearchFields:
                [
                    new("lotId", "LOT", FieldType.Select, OptionsQueryId: "QMS.InspectionLotCombo"),
                ],
                Purpose: purpose == ScreenPurpose.Auto ? ScreenPurpose.Report : purpose,
                ReadRequiredPermission: "qms:read"));
            return;
        }

        var headerFields = new FieldWidget[]
        {
            QmsField("header", "lotId", "LOT", FieldType.Select, required: true,
                optionsQueryId: "QMS.InspectionLotCombo", requiredPermission: "qms:read"),
            QmsField("header", "equipmentId", "검사 설비", FieldType.Select, required: true,
                optionsQueryId: "QMS.InspectionEquipmentCombo", requiredPermission: "qms:read"),
            QmsField("header", "lotQuantity", "LOT 수량", FieldType.Number, required: true),
            QmsField("header", "sampleQuantity", "헤더 샘플 수량", FieldType.Number, required: true),
            QmsField("header", "defectQuantity", "헤더 불량 수량", FieldType.Number, required: true),
            QmsField("header", "samplingPlanRevisionId", "샘플링 계획 개정", FieldType.Select,
                optionsQueryId: "QMS.SamplingPlanRevisionCombo", requiredPermission: "qms:read"),
            QmsField("header", "relationType", "후속 실행 유형", FieldType.Select,
                options: ["Original", "Correction", "Reinspection"]),
            QmsField("header", "parentInspectionId", "상위 검사 ID"),
            QmsField("header", "remark", "헤더 비고"),
            QmsField("header", "idempotencyKey", "멱등 키", required: true, hidden: true,
                valueGenerator: FieldValueGenerator.UuidV4),
        };
        var itemFields = new FieldWidget[]
        {
            QmsField("item", "specId", "검사 규격", FieldType.Select, required: true,
                optionsQueryId: "QMS.InspectionSpecCombo", requiredPermission: "qms:read"),
            QmsField("item", "measuredValue", "측정값 (계량형)", FieldType.Number),
            QmsField("item", "attributeResult", "판정 (속성형)", FieldType.Select, options: ["Pass", "Fail"]),
            QmsField("item", "sampleQuantity", "항목 샘플 수량", FieldType.Number, required: true),
            QmsField("item", "defectQuantity", "항목 불량 수량", FieldType.Number, required: true),
            QmsField("item", "remark", "항목 비고"),
        };

        var layout = new SectionNode
        {
            Id = $"qms-registration-{uiId}",
            Title = "검사 실행 등록",
            Children =
            [
                new RowNode
                {
                    Id = $"qms-input-row-{uiId}",
                    Children =
                    [
                        new ColumnNode
                        {
                            Id = $"qms-input-column-{uiId}", Span = 12,
                            Children =
                            [
                                new FormWidget
                                {
                                    Id = $"qms-header-form-{uiId}",
                                    SaveQueryId = commandId,
                                    RequiredPermission = "qms:manage",
                                    Fields = headerFields,
                                },
                                new CollectionWidget
                                {
                                    Id = $"qms-items-{uiId}",
                                    CollectionKey = "items",
                                    Label = "검사 항목",
                                    ItemLabel = "검사 항목",
                                    Fields = itemFields,
                                    MinItems = 1,
                                    RequiredPermission = "qms:manage",
                                },
                                new ButtonWidget
                                {
                                    Id = $"qms-save-{uiId}",
                                    Label = "검사 등록",
                                    Command = commandId,
                                    RequiredPermission = "qms:manage",
                                },
                            ],
                        },
                    ],
                },
                new RowNode
                {
                    Id = $"qms-history-row-{uiId}",
                    Children =
                    [
                        new ColumnNode
                        {
                            Id = $"qms-history-column-{uiId}", Span = 12,
                            Children =
                            [
                                new GridWidget
                                {
                                    Id = $"qms-history-{uiId}",
                                    QueryId = queryId,
                                    Columns = columns,
                                    RequiredPermission = "qms:read",
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        Register(new ScreenDefinition(
            uiId,
            title,
            Array.Empty<FieldDefinition>(),
            Layout: layout,
            SearchFields:
            [
                new("lotId", "LOT", FieldType.Select, OptionsQueryId: "QMS.InspectionLotCombo"),
            ],
            Purpose: ScreenPurpose.Register,
            ReadRequiredPermission: "qms:read",
            SaveRequiredPermission: "qms:manage"));
    }

    /// <summary>검사 실행 헤더와 반복 항목이 공유하는 FieldWidget 생성기입니다.</summary>
    private static FieldWidget QmsField(
        string scope,
        string key,
        string label,
        FieldType type = FieldType.Text,
        bool required = false,
        IReadOnlyList<string>? options = null,
        string? optionsQueryId = null,
        string? requiredPermission = null,
        bool hidden = false,
        FieldValueGenerator valueGenerator = FieldValueGenerator.None)
        => new()
        {
            Id = $"qms-{scope}-{key}",
            FieldKey = key,
            RequiredPermission = requiredPermission,
            Field = new FieldDefinition(
                key,
                label,
                type,
                required,
                Options: options,
                OptionsQueryId: optionsQueryId,
                Hidden: hidden,
                ValueGenerator: valueGenerator),
        };

    /// <summary>헤더와 항목 수량, 취소/대체 상태를 혼동 없이 보여 주는 검사 실행 이력 컬럼입니다.</summary>
    private static IReadOnlyList<GridColumnDefinition> QmsInspectionHistoryColumns()
        =>
        [
            new("INSPECTION_ID", "검사 ID", Width: 145),
            new("RESULT_ID", "결과 ID", Width: 145),
            new("ITEM_SEQUENCE", "항목 순번", Width: 85),
            new("INSPECTION_TYPE", "유형", Width: 90),
            new("LOT_ID", "LOT", Width: 130),
            new("PRODUCT_ID", "품목", Width: 120),
            new("EQUIPMENT_ID", "설비", Width: 110),
            new("SPEC_ID", "규격", Width: 120),
            new("LOT_QTY", "헤더 LOT 수량", Width: 105),
            new("SAMPLE_QTY", "헤더 샘플 수량", Width: 110),
            new("DEFECT_QTY", "헤더 불량 수량", Width: 110),
            new("ITEM_SAMPLE_QTY", "항목 샘플 수량", Width: 110),
            new("ITEM_DEFECT_QTY", "항목 불량 수량", Width: 110),
            new("MEASURED_VALUE", "측정값", Width: 90),
            new("ATTRIBUTE_RESULT", "속성 판정", Width: 90),
            new("RESULT", "헤더 결과", Width: 85),
            new("EFFECTIVE_RESULT", "유효 결과", Width: 95),
            new("IS_CANCELLED", "취소", Width: 70),
            new("IS_SUPERSEDED", "후속 실행 대체", Width: 105),
            new("IS_CONFIRMED", "확정", Width: 70),
            new("INSPECTED_AT", "검사시각", Width: 155),
            new("INSPECTOR_ID", "검사자", Width: 100),
            new("REMARK", "비고", Width: 180),
        ];

    /// <summary>
    /// 설비 작업 관리 화면입니다. 작업 범위 생성은 typed POM bridge로, 조회는 명명 쿼리로
    /// 연결한다. WorkScope 그리드만 lifecycle 일괄 명령을 받고 Carrier/툴/레거시 이력은
    /// 명시적인 빈 명령 목록으로 보호해 상태전이 버튼이 섞이지 않게 한다.
    /// </summary>
    private static LayoutNode BuildEquipmentWorkManagementLayout()
        => new SectionNode
        {
            Id = "equipment-work-management",
            Title = "작업 범위와 이력",
            Children =
            [
                new TextWidget
                {
                    Id = "equipment-work-scope-note",
                    Text = "이 설비는 생산 W/O를 작업 단위로 사용하지 않습니다. Campaign → Batch → Carrier 계층 또는 Carrier 단독 범위로 등록하며, LOT 없이 Carrier ID로 세척 이력을 추적합니다.",
                },
                new TextWidget
                {
                    Id = "equipment-work-api-note",
                    Text = "작업 범위 등록·상태 전이는 POM API가 담당하고, 하단에는 Carrier 산출 결과와 툴 사용·점검 스냅샷을 함께 표시합니다. 실적은 양품/이상 누계로 보고하며 서버가 버전과 작업자 이력을 검증합니다.",
                },
                new SectionNode
                {
                    Id = "equipment-work-scope-registration",
                    Title = "작업 범위 등록",
                    Children =
                    [
                        new FormWidget
                        {
                            Id = "equipment-work-scope-create-form",
                            SaveQueryId = PomWorkScopeMetaCommands.Create,
                            RequiredPermission = "pom:manage",
                            BindingScope = "work-scope-create",
                            Fields =
                            [
                                new() { FieldKey = "workScopeId", Field = new FieldDefinition("workScopeId", "작업 범위 ID", Required: true) },
                                new() { FieldKey = "plantId", Field = new FieldDefinition("plantId", "공장 ID", Required: true) },
                                new() { FieldKey = "scopeType", Field = new FieldDefinition(
                                    "scopeType", "범위 유형", FieldType.Select, Required: true,
                                    Options: ["Campaign", "Batch", "Carrier", "Lot", "Equipment", "Other"]) },
                                new() { FieldKey = "targetId", Field = new FieldDefinition("targetId", "대상 ID", Required: true) },
                                new() { FieldKey = "name", Field = new FieldDefinition("name", "작업명", Required: true) },
                                new() { FieldKey = "parentScopeId", Field = new FieldDefinition("parentScopeId", "상위 작업 범위 ID") },
                                new() { FieldKey = "carrierId", Field = new FieldDefinition("carrierId", "Carrier ID (상위 연결, 선택)") },
                                new() { FieldKey = "equipmentId", Field = new FieldDefinition("equipmentId", "설비 ID") },
                                new() { FieldKey = "processId", Field = new FieldDefinition("processId", "공정 ID") },
                                new() { FieldKey = "recipeId", Field = new FieldDefinition("recipeId", "레시피 ID") },
                                new() { FieldKey = "recipeVersion", Field = new FieldDefinition("recipeVersion", "레시피 버전", FieldType.Number) },
                                new() { FieldKey = "planQty", Field = new FieldDefinition("planQty", "계획 수량", FieldType.Number) },
                                new() { FieldKey = "ownerId", Field = new FieldDefinition("ownerId", "담당자 ID") },
                                new() { FieldKey = "description", Field = new FieldDefinition("description", "설명") },
                            ],
                        },
                        new ButtonWidget
                        {
                            Id = "equipment-work-scope-create-button",
                            Label = "작업 범위 등록",
                            Command = PomWorkScopeMetaCommands.Create,
                            RequiredPermission = "pom:manage",
                            BindingScope = "work-scope-create",
                        },
                    ],
                },
                new SectionNode
                {
                    Id = "equipment-work-scope-execution",
                    Title = "작업 범위 실행",
                    Children =
                    [
                        new TextWidget
                        {
                            Id = "equipment-work-scope-execution-note",
                            Text = "행을 선택한 뒤 릴리즈·시작·실적 보고·보류·보류 해제·완료·취소를 실행합니다. 양품/이상 수량은 현재 누계이며, 실행 후 최신 버전을 다시 조회합니다.",
                        },
                        new GridWidget
                        {
                            Id = "equipment-work-scope-grid",
                            QueryId = "POM.WorkScopeList",
                            RequiredPermission = "pom:read",
                            BulkCommands = BuildEquipmentWorkScopeBulkCommands(),
                            Columns =
                            [
                                new("WORK_SCOPE_ID", "작업 범위 ID", Width: 150),
                                new("PLANT_ID", "공장", Width: 95),
                                new("SCOPE_TYPE", "범위 유형", Width: 105),
                                new("TARGET_ID", "대상 ID", Width: 140),
                                new("NAME", "작업명", Width: 160),
                                new("PARENT_SCOPE_ID", "상위 범위", Width: 140),
                                new("CARRIER_ID", "Carrier ID", Width: 125),
                                new("EQUIPMENT_ID", "설비", Width: 110),
                                new("PROCESS_ID", "공정", Width: 110),
                                new("RECIPE_ID", "레시피", Width: 125),
                                new("RECIPE_VERSION", "레시피 버전", Width: 95),
                                new("PLAN_QTY", "계획 수량", Width: 95),
                                new("START_QTY", "착수 수량", Width: 95),
                                new("COMPLETE_QTY", "양품 누계", Width: 95),
                                new("SCRAP_QTY", "이상 누계", Width: 95),
                                new("STATUS", "상태", Width: 105),
                                new("IS_HOLD", "보류", Width: 70),
                                new("STARTED_AT", "시작시각", Width: 155),
                                new("COMPLETED_AT", "완료시각", Width: 155),
                                new("VERSION_NO", "버전", Width: 70),
                                new("OWNER_ID", "담당자", Width: 110),
                                new("DESCRIPTION", "설명", Width: 180),
                                new("WORK_ORDER_ID", "기존 생산지시 참조 (선택)", Width: 170),
                            ],
                        },
                    ],
                },
                new RowNode
                {
                    Id = "equipment-work-history-row",
                    Children =
                    [
                        new ColumnNode
                        {
                            Id = "equipment-carrier-history-column",
                            Span = 8,
                            Children =
                            [
                                new SectionNode
                                {
                                    Id = "equipment-carrier-history-section",
                                    Title = "Carrier 작업 이력",
                                    Children =
                                    [
                                        new GridWidget
                                        {
                                            Id = "equipment-carrier-output-grid",
                                            QueryId = "EST.EquipmentOutputEventList",
                                            RequiredPermission = "est:read",
                                            SelectionDisabled = true,
                                            BulkCommands = Array.Empty<BulkCommandDefinition>(),
                                            Columns =
                                            [
                                                new("OUTPUT_EVENT_ID", "작업 이력 ID", Width: 145),
                                                new("OCCURRED_AT", "발생시각", Width: 155),
                                                new("OUTPUT_TYPE", "작업 결과/판정", Width: 125),
                                                new("CARRIER_ID", "Carrier ID", Width: 125),
                                                new("WORK_SCOPE_ID", "작업 범위", Width: 145),
                                                new("EQUIPMENT_ID", "설비", Width: 110),
                                                new("PROCESS_LOT_ID", "LOT (선택)", Width: 125),
                                                new("WORK_ORDER_ID", "외부 작업 참조 (선택)", Width: 155),
                                                new("PROCESS_ID", "공정", Width: 110),
                                                new("RECIPE_ID", "레시피", Width: 125),
                                                new("RECIPE_VERSION", "레시피 버전", Width: 95),
                                                new("TOTAL_QTY", "처리 수량", Width: 95),
                                                new("GOOD_QTY", "정상 수량", Width: 95),
                                                new("DEFECT_QTY", "이상 수량", Width: 95),
                                                new("UNIT", "단위", Width: 75),
                                                new("ACTOR_ID", "작업자", Width: 110),
                                                new("SOURCE", "발생원", Width: 100),
                                                new("SOURCE_EVENT_ID", "원천 이벤트", Width: 145),
                                                new("CORRELATION_ID", "연계 ID", Width: 145),
                                            ],
                                        },
                                    ],
                                },
                            ],
                        },
                        new ColumnNode
                        {
                            Id = "equipment-tool-history-column",
                            Span = 4,
                            Children =
                            [
                                new SectionNode
                                {
                                    Id = "equipment-tool-usage-section",
                                    Title = "툴 사용·공정 조건",
                                    Children =
                                    [
                                        new GridWidget
                                        {
                                            Id = "equipment-tool-usage-grid",
                                            QueryId = "EMS.ToolUsageHistoryList",
                                            RequiredPermission = "ems:read",
                                            SelectionDisabled = true,
                                            BulkCommands = Array.Empty<BulkCommandDefinition>(),
                                            Columns =
                                            [
                                                new("USAGE_ID", "사용 이력 ID", Width: 145),
                                                new("USED_AT", "사용시각", Width: 155),
                                                new("TOOL_ID", "툴", Width: 115),
                                                new("EQUIPMENT_ID", "설비", Width: 110),
                                                new("WORK_SCOPE_ID", "작업 범위", Width: 145),
                                                new("CARRIER_ID", "Carrier ID", Width: 125),
                                                new("ACTIVITY_TYPE", "활동 유형", Width: 110),
                                                new("CLEANING_PROGRAM_ID", "세척 프로그램", Width: 135),
                                                new("CLEANING_RESULT", "세척 판정", Width: 105),
                                                new("RECIPE_ID", "레시피", Width: 125),
                                                new("USE_COUNT", "사용 횟수", Width: 95),
                                                new("USE_MINUTES", "사용 시간(분)", Width: 105),
                                                new("USED_BY", "사용자", Width: 110),
                                                new("TRACE_ID", "TRACE ID", Width: 145),
                                                new("CONDITION_SNAPSHOT_JSON", "공정 조건 스냅샷", Width: 240),
                                            ],
                                        },
                                    ],
                                },
                                new SectionNode
                                {
                                    Id = "equipment-tool-inspection-section",
                                    Title = "툴 점검·교정 이력",
                                    Children =
                                    [
                                        new GridWidget
                                        {
                                            Id = "equipment-tool-inspection-grid",
                                            QueryId = "EMS.ToolInspectionHistoryList",
                                            RequiredPermission = "ems:read",
                                            SelectionDisabled = true,
                                            BulkCommands = Array.Empty<BulkCommandDefinition>(),
                                            Columns =
                                            [
                                                new("INSPECTION_ID", "점검 이력 ID", Width: 145),
                                                new("INSPECTION_TYPE", "점검 유형", Width: 110),
                                                new("RESULT", "판정", Width: 90),
                                                new("MEASURED_VALUE", "측정값", Width: 100),
                                                new("CERTIFICATE_NO", "성적서 번호", Width: 135),
                                                new("INSPECTED_AT", "점검시각", Width: 155),
                                                new("INSPECTED_BY", "점검자", Width: 110),
                                            ],
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
                new SectionNode
                {
                    Id = "equipment-legacy-work-reference-section",
                    Title = "기존 생산지시 참조 (읽기 전용)",
                    Children =
                    [
                        new TextWidget
                        {
                            Id = "equipment-legacy-work-reference-note",
                            Text = "기존 생산지시 데이터는 외부 연계 또는 과거 이력 확인용으로만 표시합니다. 이 화면에서는 생산지시를 생성하거나 상태를 변경하지 않습니다.",
                        },
                        new GridWidget
                        {
                            Id = "equipment-legacy-work-reference-grid",
                            QueryId = "POM.WorkOrderList",
                            RequiredPermission = "pom:read",
                            SelectionDisabled = true,
                            BulkCommands = Array.Empty<BulkCommandDefinition>(),
                            Columns =
                            [
                                new("WORK_ORDER_ID", "외부 작업 참조 ID", Width: 155),
                                new("PRODUCTION_ORDER_ID", "생산계획 참조", Width: 145),
                                new("EQUIPMENT_ID", "설비", Width: 110),
                                new("PLAN_QTY", "계획 수량", Width: 95),
                                new("COMPLETE_QTY", "완료 수량", Width: 95),
                                new("STATUS", "상태", Width: 95),
                                new("STARTED_AT", "시작시각", Width: 155),
                            ],
                        },
                    ],
                },
            ],
        };

    private static IReadOnlyList<BulkCommandDefinition> BuildEquipmentWorkScopeBulkCommands()
        =>
        [
            new("릴리즈", PomWorkScopeMetaCommands.Release, "선택한 작업 범위를 릴리즈하시겠습니까?", "pom:manage"),
            new("시작", PomWorkScopeMetaCommands.Start, "선택한 작업 범위를 시작하시겠습니까?", "pom:execute"),
            new("실적 보고", PomWorkScopeMetaCommands.Report, "선택한 작업 범위의 현재 양품/이상 누계를 보고하시겠습니까?", "pom:execute"),
            new("보류", PomWorkScopeMetaCommands.Hold, "선택한 작업 범위를 보류하시겠습니까?", "pom:execute"),
            new("보류 해제", PomWorkScopeMetaCommands.ReleaseHold, "선택한 작업 범위의 보류를 해제하시겠습니까?", "pom:execute"),
            new("완료", PomWorkScopeMetaCommands.Complete, "선택한 작업 범위를 완료하시겠습니까?", "pom:execute"),
            new("취소", PomWorkScopeMetaCommands.Cancel, "선택한 작업 범위를 취소하시겠습니까?", "pom:manage"),
        ];

    private static IReadOnlyList<FieldDefinition> BuildEquipmentWorkManagementSearchFields()
        =>
        [
            new("plantId", "공장 ID"),
            new("scopeType", "범위 유형", FieldType.Select,
                Options: ["Campaign", "Batch", "Carrier", "Lot", "Equipment", "Other"]),
            new("targetId", "대상 ID"),
            new("parentScopeId", "상위 작업 범위 ID"),
            new("status", "상태", FieldType.Select,
                Options: ["Created", "Released", "Started", "Completed", "Cancelled"]),
            new("equipmentId", "설비 ID"),
            new("carrierId", "Carrier ID"),
            new("workScopeId", "작업 범위 ID"),
            new("outputType", "작업 결과 유형"),
            new("toolId", "툴 ID"),
            new("activityType", "툴 활동 유형"),
            new("inspectionType", "툴 점검 유형"),
            new("from", "시작일", FieldType.Date),
            new("to", "종료일", FieldType.Date),
        ];

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

    public Task<IReadOnlySet<string>> GetKnownUiIdsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<string>>(
            _defs.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
}

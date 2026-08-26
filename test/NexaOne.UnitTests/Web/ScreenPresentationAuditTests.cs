using System.Text.Json;
using NexaOne.Web.Components.Meta;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>
/// 메뉴와 코드 화면이 서로 다른 이름을 노출하는 회귀를 전 화면에서 감시합니다.
/// 현재 차이는 canonical MES 용어와 정리 사유를 명시한 경우에만 허용하며, 새 차이는 즉시 실패합니다.
/// </summary>
public sealed class ScreenPresentationAuditTests
{
    private static readonly IReadOnlyDictionary<string, TerminologyDecision> KnownDivergences =
        new Dictionary<string, TerminologyDecision>(StringComparer.Ordinal)
        {
            ["EES_EPT_OVERALL_EQUIPMENT_EFFECTIVENESS"] = Decision("설비 종합 지표(OEE)", "화면의 지표 약어를 메뉴에도 반영할 대상"),
            ["EES_POPUP_MONITORING_DASHBOARD"] = Decision("설비 모니터링(실시간)", "레거시 메뉴와 실제 설비 모니터링 화면의 의미가 다름"),
            ["EPT_STD_EQUIPMENT_PROPERTY"] = Decision("설비별 EPT 속성 관리", "관리 범위를 드러내는 메뉴 용어가 더 명확함"),
            ["FACTORY_DASHBOARD_MENU_SAMPLE_TEST"] = Decision("대시보드 샘플", "샘플 화면 접미를 제거할 대상"),
            ["FACTORY_EMS_BM_ORDER_GRIDTYPE"] = Decision("설비 수리 요청 그리드", "레거시 밑줄 표기를 공백으로 정규화"),
            ["FACTORY_EMS_BM_ORDER_REPAIR_REGISTER_GRIDTYPE"] = Decision("설비 수리 등록 그리드", "레거시 밑줄 표기를 공백으로 정규화"),
            ["FACTORY_EMS_BM_ORDER_RESULT_GRIDTYPE"] = Decision("설비 보전 결과 그리드", "레거시 밑줄 표기를 공백으로 정규화"),
            ["FACTORY_EMS_STD_SPARE_PART_MOVE_GRIDTYPE"] = Decision("Spare Part 이동 그리드", "레거시 밑줄 표기를 공백으로 정규화"),
            ["FACTORY_EMS_STD_SPARE_PART_SCRAP_GRIDTYPE"] = Decision("Spare Part 폐기 그리드", "레거시 밑줄 표기를 공백으로 정규화"),
            ["FACTORY_RPT_LOT_TRACE"] = Decision("LOT 추적", "LOT 대문자 표준"),
            ["FACTORY_STD_LABEL_MAPPING_MANAGEMENT"] = Decision("라벨 매핑 관리", "외래어 표기 매핑으로 통일"),
            ["FACTORY_WPM_DEFECT_REPAIR"] = Decision("불량 수리", "실제 surface가 조회 전용이므로 등록 표현 제거"),
            ["LOG_VIEWER"] = Decision("로그 뷰어", "한국어 띄어쓰기 정규화"),
            ["MICUBE_STANDARD_EQUIPMENT_STATE_ALARM_MAPPING"] = Decision("설비 알람-상태 매핑", "매핑 관계 구분자 표준화"),
            ["MICUBE_STANDARD_EQUIPMENT_STATE_EVENT_MAPPING"] = Decision("설비 이벤트-상태 매핑", "매핑 관계 구분자 표준화"),
            ["MICUBE_STANDARD_EQUIPMENT_STATE_MATRIX"] = Decision("설비 상태 매트릭스", "불필요한 정보 접미 제거"),
            ["MICUBE_STANDARD_STD_EQUIPMENT_MAILING"] = Decision("설비 메일링 관리", "실제 화면 모델과 레거시 메뉴 의미가 다름"),
            ["MICUBE_STANDARD_STD_USER_ALARM_MAILING"] = Decision("알람 메일 수신자 관리", "실제 화면 모델과 레거시 메뉴 의미가 다름"),
            ["MICUBE_STANDARD_USER_EQUIPMENT_ALARM_MAIL_MAP"] = Decision("사용자-설비 알람 메일 매핑", "띄어쓰기와 관계 구분자 표준화"),
            ["MICUBE_STANDARD_USER_EQUIPMENT_MAIL_MAP"] = Decision("사용자-설비 메일 매핑", "실제 화면 모델과 레거시 메뉴 의미가 다름"),
            ["POC_COATING_PROCESS"] = Decision("코팅 공정 진행", "POC 화면의 대상 공정을 명시"),
            ["POC_INPROCESS_LOT"] = Decision("재공 LOT 현황", "WIP의 MES 표준 한국어 용어"),
            ["POC_LOT_TRACE_TREE"] = Decision("생산 LOT 추적(트리)", "영문 Tree와 하이픈 표기를 한국어로 정규화"),
            ["POC_MIXING_PROCESS"] = Decision("믹싱 공정 진행", "배합/믹싱 중 화면 도메인 명칭으로 통일"),
            ["POC_PPM_WORK_ORDER"] = Decision("작업지시 관리", "작업지시는 MES 복합 업무용어로 붙여 씀"),
            ["POC_ROLLING_PROCESS"] = Decision("롤투롤 공정 진행", "영문 Roll To Roll을 현장 한국어로 통일"),
            ["QMS_MEQ_CALIBRATION_STATUS"] = Decision("계측기 검교정 현황", "교정/검교정 용어 통일"),
            ["QMS_MEQ_MEASURE_FAILURE_RATE"] = Decision("계측기 측정 불량 현황", "교정 불량과 측정 불량의 업무 의미 구분"),
            ["QMS_MEQ_MEASURE_REPAIR_DETAILS"] = Decision("계측기 수리 현황", "불필요한 내역 접미 제거"),
            ["QMS_QCA_NCR_OVERVIEW"] = Decision("NCR 현황", "불필요한 종합 접미 제거"),
            ["SYSTEM_2_BATCH_PROC_MANAGEMENT"] = Decision("배치 작업 관리", "한국어 띄어쓰기 정규화"),
            ["SYSTEM2_CONTENTMAPPINGSERVICE_MANAGEMENT"] = Decision("콘텐츠 매핑 서비스 관리(대체됨)", "대체된 아키텍처 안내 화면"),
            ["SYSTEM2_MONITOR_REQLOG"] = Decision("요청 로그 뷰어", "일반 로그 뷰어와 요청 로그 화면 구분"),
        };

    private static readonly string[] HostRoutedMenuUiIds =
    [
        "FACTORY_DASHBOARD_LAYOUT_EDIT",
        "SYSTEM_2_AUTH_MANAGEMENT",
        "SYSTEM_2_SO_MANAGEMENT",
    ];

    [Fact]
    public async Task Every_menu_screen_title_matches_or_has_an_explicit_canonical_MES_decision()
    {
        var provider = new InMemoryScreenDefinitionProvider();
        var knownIds = await provider.GetKnownUiIdsAsync();
        var menuRows = JsonSerializer.Deserialize<List<MenuSeedRow>>(
            File.ReadAllText(RepoFile("src/00.Main/NexaOne.Server/config/seed/nexaone-menu.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var leaves = menuRows.Where(row =>
            string.Equals(row.MenuType, "Screen", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(row.UiId)).ToArray();

        var hostRouted = leaves
            .Where(row => !knownIds.Contains(row.UiId))
            .Select(row => row.UiId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        hostRouted.Should().Equal(HostRoutedMenuUiIds.OrderBy(id => id, StringComparer.Ordinal),
            "코드 메타 정의가 없는 메뉴는 명시적인 Host 페이지 경로여야 한다");

        var mismatches = leaves
            .Where(row => knownIds.Contains(row.UiId))
            .Select(row => new
            {
                row.UiId,
                MenuTitle = row.MenuName,
                ScreenTitle = provider.Get(row.UiId)!.Title,
            })
            .Where(item => !string.Equals(item.MenuTitle, item.ScreenTitle, StringComparison.Ordinal))
            .ToDictionary(item => item.UiId, StringComparer.Ordinal);

        mismatches.Keys.Should().BeEquivalentTo(KnownDivergences.Keys,
            "새 용어 불일치는 허용하지 않고, 해소된 항목은 예외 목록에서도 제거해야 한다");
        KnownDivergences.Should().HaveCount(33,
            "운영 용어 18건을 해소한 뒤 남은 화면별 의도 차이만 명시적으로 관리한다");

        foreach (var (uiId, decision) in KnownDivergences)
        {
            _ = mismatches[uiId];
            decision.Canonical.Should().NotBeNullOrWhiteSpace($"{uiId} 예외에는 목표 canonical 용어가 필요하다");
            decision.Reason.Should().NotBeNullOrWhiteSpace($"{uiId} 예외에는 정리 근거가 필요하다");
        }
    }

    [Fact]
    public void Routing_step_screen_is_a_regular_master_data_menu_between_routing_and_work_order_routing()
    {
        var menuRows = JsonSerializer.Deserialize<List<MenuSeedRow>>(
            File.ReadAllText(RepoFile("src/00.Main/NexaOne.Server/config/seed/nexaone-menu.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var routingMenus = menuRows
            .Where(row => string.Equals(row.ParentMenuId, "FACTORY_STD_SINGLE_SEGMENT", StringComparison.Ordinal))
            .OrderBy(row => row.DisplaySequence)
            .ToArray();
        var routingStep = routingMenus.Single(row => row.MenuId == "FACTORY_STD_ROUTING_STEP");

        routingStep.MenuName.Should().Be("라우팅 스텝 관리");
        routingStep.UiId.Should().Be("FACTORY_STD_ROUTING_STEP");
        routingStep.MenuType.Should().Be("Screen");
        routingStep.DisplaySequence.Should().Be(6);
        routingMenus.Select(row => row.MenuId).Should().ContainInOrder(
            "FACTORY_STD_SINGLE_PROCESSPATH",
            "FACTORY_STD_ROUTING_STEP",
            "FACTORY_STD_WO_PROCESS_PATH",
            "FACTORY_STD_SINGLE_BILL_OF_MATERIAL",
            "FACTORY_STD_BOR_CONDITION",
            "FACTORY_STD_BOR_RESOURCE");
    }

    [Fact]
    public async Task Wide_grids_are_counted_and_manage_cards_use_the_shared_semantic_summary_policy()
    {
        var provider = new InMemoryScreenDefinitionProvider();
        var definitions = (await provider.GetKnownUiIdsAsync())
            .Select(provider.Get)
            .Where(definition => definition is not null)
            .Cast<ScreenDefinition>()
            .DistinctBy(definition => definition.UiId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var wideScreens = definitions
            .Where(definition => GridColumnSets(definition).Any(columns =>
                columns.Count(column => column.Visible) > 7))
            .ToArray();
        var wideManageScreens = wideScreens
            .Where(definition => definition.Purpose == ScreenPurpose.Manage)
            .ToArray();

        wideScreens.Should().HaveCount(100,
            "전 화면 Grid 폭 감사 기준(8열 이상)을 변경할 때 UX 검토 목록도 함께 갱신해야 한다");
        wideManageScreens.Select(definition => definition.UiId).Should().BeEquivalentTo(
            "EPT_STD_TAKT_TARGET",
            "FACTORY_COM_ACTION_DEF",
            "FACTORY_PPM_WORK_ORDER",
            "FACTORY_PRC_PURCHASE_ORDER",
            "FACTORY_SLS_SALES_ORDER",
            "FACTORY_STD_BOR_CONDITION",
            "MES_MDM_COM_VENDOR",
            "POC_PPM_WORK_ORDER");

        foreach (var definition in wideManageScreens)
        {
            var columns = definition.Columns!.Where(column => column.Visible).ToArray();
            var primary = MetaGridColumnPolicy.CardPrimary(columns);
            var summary = MetaGridColumnPolicy.CardSummary(columns, primary);
            summary.Should().HaveCountLessThanOrEqualTo(MetaGridColumnPolicy.DefaultCardFieldCount);

            var status = columns.FirstOrDefault(column =>
                column.Key.Contains("STATUS", StringComparison.OrdinalIgnoreCase)
                || column.Key.Contains("STATE", StringComparison.OrdinalIgnoreCase)
                || column.Key.Contains("RESULT", StringComparison.OrdinalIgnoreCase));
            if (status is not null && !string.Equals(primary?.Key, status.Key, StringComparison.OrdinalIgnoreCase))
                summary.Should().Contain(column => column.Key == status.Key,
                    $"{definition.UiId} 카드에서 상태/판정이 접힌 필드로 밀리면 안 된다");
        }
    }

    private static TerminologyDecision Decision(string canonical, string reason) => new(canonical, reason);

    private static IEnumerable<IReadOnlyList<GridColumnDefinition>> GridColumnSets(ScreenDefinition definition)
    {
        if (definition.Columns is { Count: > 0 }) yield return definition.Columns;
        foreach (var columns in GridColumnSets(definition.Layout)) yield return columns;
    }

    private static IEnumerable<IReadOnlyList<GridColumnDefinition>> GridColumnSets(LayoutNode? node)
    {
        if (node is null) yield break;
        if (node is GridWidget { Columns.Count: > 0 } grid) yield return grid.Columns;

        var children = node switch
        {
            SectionNode section => section.Children,
            RowNode row => row.Children,
            ColumnNode column => column.Children,
            _ => null,
        };
        if (children is null) yield break;
        foreach (var child in children)
        foreach (var columns in GridColumnSets(child))
            yield return columns;
    }

    private static string RepoFile(string relativePath)
        => RepositorySource.GetFile(relativePath);

    private sealed record TerminologyDecision(string Canonical, string Reason);
    private sealed record MenuSeedRow(
        string MenuId,
        string MenuName,
        string? ParentMenuId,
        int DisplaySequence,
        string MenuType,
        string UiId);
}

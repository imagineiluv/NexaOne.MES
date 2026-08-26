using System.Collections.ObjectModel;

namespace NexaOne.Web.Services.Meta;

/// <summary>
/// 코드 시드에서 하위 호환 기본값 <see cref="ScreenPurpose.Auto"/>로 남아 있던 화면의
/// 기능 계약 기반 목적 결정입니다. 화면 이름이 아니라 실제 active surface의 조회, 입력,
/// 저장, 삭제, 명령 경로를 기준으로 고정합니다.
/// </summary>
public static class SeedScreenPurposeDecisions
{
    private static readonly string[] InquiryUiIds =
    [
        "EES_EPT_CHANGE_EQUIPMENT_STATE",
        "EES_FDC_ACTIVE_SPEC_MANAGEMENT",
        "EES_FDC_EVENT_PARAMETER_MANAGEMENT",
        "EES_FDC_IDLE_SPEC_MANAGEMENT",
        "EES_FDC_INTERESTED_PARAMETER_MANAGEMENT",
        "EES_FDC_PARAMETER_STATE_CONDITION",
        "EES_FDC_SUMMARY_PARAMETER_MANAGEMENT",
        "EES_FDC_SUMMARY_SPEC_MANAGEMENT",
        "EES_FDC_TRACE_GROUP",
        "EES_FDC_TRACE_PARAMETER_MANAGEMENT",
        "EES_FDC_VIRTUAL_PARAMETER_MANAGEMENT",
        "EPT_STD_EQUIPMENT_PROPERTY",
        "EPT_STD_INDEX_MANAGEMENT",
        "EPT_STD_LAYOUT_EDIT",
        "EPT_STD_LAYOUT_MANAGEMENT",
        "FACTORY_COM_ALARM_ACTION",
        "FACTORY_COM_ALARM_CLASS",
        "FACTORY_COM_ALARM_DEF",
        "FACTORY_COM_CODE_CLASS",
        "FACTORY_COM_CODE_CODE",
        "FACTORY_COM_CODE_ID_DEFINITION",
        "FACTORY_COM_CODE_STATE",
        "FACTORY_COM_CODE_STATE_MODEL",
        "FACTORY_COM_CODE_STATE_TRANSITION",
        "FACTORY_COM_CURRENCY_CODE",
        "FACTORY_COM_LABEL",
        "FACTORY_DLV_DELIVERY_ORDER",
        "FACTORY_DLV_DELIVERY_RESULT",
        "FACTORY_EMS_BM_ORDER_GRIDTYPE",
        "FACTORY_EMS_BM_ORDER_REPAIR",
        "FACTORY_EMS_BM_ORDER_REPAIR_REGISTER_GRIDTYPE",
        "FACTORY_EMS_BM_ORDER_REQUEST",
        "FACTORY_EMS_BM_ORDER_RESULT",
        "FACTORY_EMS_BM_ORDER_RESULT_GRIDTYPE",
        "FACTORY_EMS_PM_ORDER_PLAN",
        "FACTORY_EMS_PM_ORDER_PLAN_GRIDTYPE",
        "FACTORY_EMS_PM_ORDER_RESULT_RESULT",
        "FACTORY_EMS_STD_EQP_MAINT_ITEM",
        "FACTORY_EMS_STD_MAINT_ITEM",
        "FACTORY_EMS_STD_MAINT_ITEM_CLASS",
        "FACTORY_EMS_STD_SPARE_PART",
        "FACTORY_EMS_STD_SPARE_PART_CLASS",
        "FACTORY_EMS_STD_SPARE_PART_INCOMING",
        "FACTORY_EMS_STD_SPARE_PART_MOVE",
        "FACTORY_EMS_STD_SPARE_PART_MOVE_GRIDTYPE",
        "FACTORY_EMS_STD_SPARE_PART_SCRAP",
        "FACTORY_EMS_STD_SPARE_PART_SCRAP_GRIDTYPE",
        "FACTORY_IVT_CONSUMABLE_LOT",
        "FACTORY_IVT_INVENTORY_STATUS",
        "FACTORY_IVT_MATERIAL_DISPENSING",
        "FACTORY_IVT_MATERIAL_DISPENSING_REQUEST",
        "FACTORY_IVT_MATERIAL_INCOMING_MANAGEMENT",
        "FACTORY_IVT_MATERIAL_LOT_MANAGEMENT",
        "FACTORY_IVT_MOVE_ORDER",
        "FACTORY_MDM_AREA",
        "FACTORY_MDM_BILL_OF_MATERIAL",
        "FACTORY_MDM_CARRIER",
        "FACTORY_MDM_CARRIER_CLASS",
        "FACTORY_MDM_EQUIPMENT_CLASS",
        "FACTORY_MDM_EQUIPMENT_DEF",
        "FACTORY_MDM_ITEM_CLASS",
        "FACTORY_MDM_ITEM_DEF",
        "FACTORY_MDM_PROCESS_CLASS",
        "FACTORY_MDM_PROCESS_DEF",
        "FACTORY_MDM_PROCESS_PATH",
        "FACTORY_MDM_QTIME_ACTION",
        "FACTORY_MDM_REASON_CODE",
        "FACTORY_MDM_REASON_CODE_CLASS",
        "FACTORY_MDM_SEGMENT_CLASS",
        "FACTORY_MDM_SEGMENT_DEF",
        "FACTORY_MDM_SEGMENT_QTIME",
        "FACTORY_PPM_PRODUCTION_ORDER",
        "FACTORY_QCA_IMPORT_INSPECTION_MAPPING",
        "FACTORY_QCA_INSPECTION_CLASS",
        "FACTORY_QCA_INSPECTION_ITEM",
        "FACTORY_QCA_PROCESS_INSPECTION_MAPPING",
        "FACTORY_QCA_SHIPMENT_INSPECTION_MAPPING",
        "FACTORY_SLS_SALES_REQUEST",
        "FACTORY_STD_BOR_RESOURCE",
        "FACTORY_STD_SINGLE_AREA",
        "FACTORY_STD_SINGLE_BILL_OF_MATERIAL",
        "FACTORY_STD_SINGLE_CODE",
        "FACTORY_STD_SINGLE_CUSTOMER",
        "FACTORY_STD_SINGLE_DELIVERY",
        "FACTORY_STD_SINGLE_DELIVERY_ITEM",
        "FACTORY_STD_SINGLE_EQUIPMENT",
        "FACTORY_STD_SINGLE_EQUIPMENT_DEF",
        "FACTORY_STD_SINGLE_EQUIPMENTCLASS",
        "FACTORY_STD_SINGLE_ITEM",
        "FACTORY_STD_SINGLE_ITEM_DEF",
        "FACTORY_STD_SINGLE_ITEMCLASS",
        "FACTORY_STD_SINGLE_PLANT",
        "FACTORY_STD_SINGLE_PROCESS",
        "FACTORY_STD_SINGLE_PROCESSCLASS",
        "FACTORY_STD_SINGLE_PROCESSPATH",
        "FACTORY_STD_SINGLE_PRODUCT_SPEC",
        "FACTORY_STD_SINGLE_REASONCODE",
        "FACTORY_STD_SINGLE_REASONCODECLASS",
        "FACTORY_STD_SINGLE_SEGMENT",
        "FACTORY_STD_SINGLE_SEGMENT_DEF",
        "FACTORY_STD_SINGLE_SEGMENTCLASS",
        "FACTORY_STD_SINGLE_SHIFT",
        "FACTORY_STD_SINGLE_WORKER",
        "FACTORY_STD_WO_PROCESS_PATH",
        "FACTORY_STD_WORK_CALENDAR",
        "FACTORY_STD_WORKER_CLASS",
        "FACTORY_STD_WORKER_DEF",
        "FACTORY_WPM_DEFECT_REPAIR",
        "FACTORY_WPM_LOT_HOLD",
        "FACTORY_WPM_LOT_HOLD_RELEASE",
        "FACTORY_WPM_LOT_MANAGEMENT",
        "MICUBE_STANDARD_EQUIPMENT_STATE",
        "MICUBE_STANDARD_EQUIPMENT_STATE_MATRIX",
        "MICUBE_STANDARD_SERVICE_MANAGEMENT",
        "MICUBE_STANDARD_STD_EQUIPMENT_MAILING",
        "MICUBE_STANDARD_STD_USER_ALARM_MAILING",
        "MICUBE_STANDARD_USER_EQUIPMENT_MAIL_MAP",
        "QMS_4M_CHANGE_HISTORY",
        "QMS_CLM_CLAIM_REGISTRATION",
        "QMS_CLM_CLAIM_RESULT",
        "QMS_GAUGE_CALIBRATION_PLAN",
        "QMS_GAUGE_CALIBRATION_RESULT",
        "QMS_GAUGE_MEASURE_EQUIPMENT_MANAGEMENT",
        "QMS_GAUGE_REPAIR_RESULT",
        "QMS_GAUGE_RNR_PLAN",
        "QMS_GAUGE_RNR_RESULT",
        "QMS_INSP_LONGTERM_PRODUCT_INSP_RESULT",
        "QMS_LONGTERM_INSP_RESULT",
        "QMS_QCA_NCR_ISSUE",
        "QMS_QCA_RELEASE_HOLD_REG",
        "QMS_SPM_ADMIN_ACTION_RESULT_REGISTRATION",
        "QMS_SPM_EVL_DEF",
        "QMS_SPM_EVL_ITEM",
        "QMS_SPM_EVL_PARA",
        "QMS_SPM_EVL_RESULT",
        "QMS_STD_INSP_DEF",
        "QMS_STD_INSP_INCOMING_METHOD",
        "QMS_STD_INSP_ITEM",
        "QMS_STD_INSP_SPEC",
        "SYSTEM_2_AUTH_MANAGEMENT_NEW",
        "SYSTEM_2_CODE_MANAGEMENT",
        "SYSTEM_2_FILE_MENU",
        "SYSTEM_2_LANGUAGE_CLASS_MANAGEMENT",
        "SYSTEM_2_LANGUAGE_MANAGEMENT",
        "SYSTEM_2_MESSAGE_CLASS_MANAGEMENT",
        "SYSTEM_2_MESSAGE_MANAGEMENT",
        "SYSTEM_2_NOTICE_MANAGEMENT",
        "SYSTEM_2_RULE_MANAGEMENT",
        "SYSTEM_2_UIID_MANAGEMENT",
        "SYSTEM_2_USER_MANAGEMENT",
    ];

    private static readonly string[] ManageUiIds =
    [
        "EES_EPT_INTERESTED_INDEX_MANAGEMENT",
        "EES_FDC_REAL_TIME_USER_MONITORING",
        "EES_FDC_VIRTUAL_EVENT_MANAGEMENT",
        "FACTORY_MDM_PLANT",
        "FACTORY_PRC_PURCHASE_ORDER",
        "SYSTEM_2_BATCH_PROC_MANAGEMENT",
        "SYSTEM_2_MENU_AUTH_MANAGEMENT",
        "SYS_MENU_MGMT",
    ];

    /// <summary>기능 계약이 완성되어 명시 목적값으로 전환한 코드 시드 결정입니다.</summary>
    public static IReadOnlyDictionary<string, ScreenPurpose> ExplicitDecisions { get; } =
        BuildExplicitDecisions();

    /// <summary>
    /// 기존 호환 동작을 보존해야 하며 현재 enum 중 하나를 붙이면 기능을 과장하게 되는 예외입니다.
    /// 새 Auto 시드는 반드시 여기에 구체적인 유지 사유를 추가해야 합니다.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RetainedAutoReasons { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEMO_PARAM"] = "입력 메타데이터 렌더링 예제이며 조회·저장·명령 경로를 의도적으로 제공하지 않는다.",
            ["SYSTEM2_CONTENTMAPPINGSERVICE_MANAGEMENT"] = "명명 쿼리 아키텍처로 대체된 기능의 정적 안내 화면이며 데이터 조회·변경 surface가 없다.",
        });

    /// <summary>
    /// 모든 canonical 코드 시드를 등록한 뒤 목적 결정을 적용하고, 미결정 Auto나 capability 불일치를
    /// 부팅 시점에 실패시켜 새 화면이 조용히 Auto로 누적되는 것을 막습니다.
    /// </summary>
    public static void ApplyTo(IDictionary<string, ScreenDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        foreach (var (uiId, purpose) in ExplicitDecisions)
        {
            if (!definitions.TryGetValue(uiId, out var definition))
                throw new InvalidOperationException($"화면 목적 결정의 코드 시드를 찾을 수 없습니다: {uiId}");
            if (definition.Purpose != ScreenPurpose.Auto)
                throw new InvalidOperationException($"Auto가 아닌 화면에 중복 목적 결정을 선언했습니다: {uiId}={definition.Purpose}");

            var decided = definition with { Purpose = purpose };
            var errors = ScreenDefinitionCapabilityValidator.Validate(decided)
                .Where(diagnostic => diagnostic.Severity == ScreenCapabilityDiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.Code)
                .ToArray();
            if (errors.Length > 0)
            {
                throw new InvalidOperationException(
                    $"화면 목적 결정이 capability 계약과 일치하지 않습니다: {uiId}={purpose} ({string.Join(", ", errors)})");
            }

            definitions[uiId] = decided;
        }

        foreach (var (uiId, reason) in RetainedAutoReasons)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException($"Auto 유지 사유가 비어 있습니다: {uiId}");
            if (!definitions.TryGetValue(uiId, out var definition))
                throw new InvalidOperationException($"Auto 유지 결정의 코드 시드를 찾을 수 없습니다: {uiId}");
            if (definition.Purpose != ScreenPurpose.Auto)
                throw new InvalidOperationException($"Auto 유지 결정과 실제 목적이 다릅니다: {uiId}={definition.Purpose}");
        }

        var unexpectedAuto = definitions.Values
            .DistinctBy(definition => definition.UiId, StringComparer.OrdinalIgnoreCase)
            .Where(definition => definition.Purpose == ScreenPurpose.Auto)
            .Where(definition => !RetainedAutoReasons.ContainsKey(definition.UiId))
            .Select(definition => definition.UiId)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unexpectedAuto.Length > 0)
        {
            throw new InvalidOperationException(
                $"목적 결정 또는 유지 사유가 없는 Auto 코드 시드: {string.Join(", ", unexpectedAuto)}");
        }
    }

    private static IReadOnlyDictionary<string, ScreenPurpose> BuildExplicitDecisions()
    {
        var decisions = new Dictionary<string, ScreenPurpose>(StringComparer.OrdinalIgnoreCase);
        Add(decisions, InquiryUiIds, ScreenPurpose.Inquiry);
        Add(decisions, ManageUiIds, ScreenPurpose.Manage);
        return new ReadOnlyDictionary<string, ScreenPurpose>(decisions);
    }

    private static void Add(
        IDictionary<string, ScreenPurpose> decisions,
        IEnumerable<string> uiIds,
        ScreenPurpose purpose)
    {
        foreach (var uiId in uiIds)
        {
            if (!decisions.TryAdd(uiId, purpose))
                throw new InvalidOperationException($"화면 목적 결정을 중복 선언했습니다: {uiId}");
        }
    }
}

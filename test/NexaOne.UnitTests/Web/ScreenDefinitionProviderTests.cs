using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

/// <summary>Phase 3 — 메타데이터 화면 정의 제공자(시드/해석/대소문자/등록)를 검증. .razor 렌더는 빌드 검증 영역.</summary>
public sealed class ScreenDefinitionProviderTests
{
    [Fact]
    public void Seeded_demo_screen_resolves_with_required_fields()
    {
        var provider = new InMemoryScreenDefinitionProvider();

        var def = provider.Get("DEMO_PARAM");

        def.Should().NotBeNull();
        def!.Fields.Should().NotBeEmpty();
        def.Fields.Should().Contain(f => f.Key == "parameterId" && f.Required);
        def.Fields.Should().Contain(f => f.Key == "isActive" && f.Type == FieldType.Boolean);
    }

    [Fact]
    public void Get_is_case_insensitive()
        => new InMemoryScreenDefinitionProvider().Get("demo_param").Should().NotBeNull();

    [Fact]
    public void Unknown_uiId_returns_null_and_tryget_false()
    {
        var provider = new InMemoryScreenDefinitionProvider();

        provider.Get("NOPE").Should().BeNull();
        provider.TryGet("NOPE", out var d).Should().BeFalse();
        d.Should().BeNull();
    }

    [Fact]
    public void Register_adds_and_overwrites_definition()
    {
        var provider = new InMemoryScreenDefinitionProvider();
        provider.Register(new ScreenDefinition("X1", "Custom", new FieldDefinition[] { new("a", "A") }));

        provider.Get("X1")!.Title.Should().Be("Custom");
    }

    [Fact]
    public async Task Known_ui_ids_are_returned_as_a_case_insensitive_snapshot()
    {
        var provider = new InMemoryScreenDefinitionProvider();
        provider.Register(new ScreenDefinition("X1", "Custom", Array.Empty<FieldDefinition>()));

        var ids = await provider.GetKnownUiIdsAsync();

        ids.Should().Contain("x1");
        ids.Should().Contain("demo_param");
    }

    [Theory]
    [InlineData("QMS_INSP_IMPORT_INSPECTION", QmsInspectionMetaCommands.RecordIncoming)]
    [InlineData("QMS_INSP_PROCESS_INSPECTION", QmsInspectionMetaCommands.RecordProcess)]
    [InlineData("QMS_INSP_PROCESS_INSPECTION_LOT", QmsInspectionMetaCommands.RecordProcess)]
    [InlineData("QMS_INSP_SHIPPING_INSPECTION", QmsInspectionMetaCommands.RecordShipping)]
    [InlineData("FACTORY_QCA_IMPORT_INSPECTION", QmsInspectionMetaCommands.RecordIncoming)]
    [InlineData("FACTORY_QCA_SEGMENT_INSPECTION", QmsInspectionMetaCommands.RecordProcess)]
    [InlineData("FACTORY_QCA_DELIVERY_INSPECTION", QmsInspectionMetaCommands.RecordShipping)]
    public void Qms_registration_screens_expose_a_typed_create_form(string uiId, string commandId)
    {
        var definition = new InMemoryScreenDefinitionProvider().Get(uiId);

        definition.Should().NotBeNull();
        definition!.Purpose.Should().Be(ScreenPurpose.Register);
        definition.Fields.Should().BeEmpty("등록 입력의 단일 출처는 Layout 폼과 collection이어야 한다");
        definition.SaveQueryId.Should().BeNull("레이아웃 모드에서 숨은 평면 저장 surface를 만들지 않는다");
        definition.Layout.Should().NotBeNull();

        var nodes = AllLayoutNodes(definition.Layout!).ToArray();
        var form = nodes.OfType<FormWidget>().Should().ContainSingle().Subject;
        form.SaveQueryId.Should().Be(commandId);
        form.RequiredPermission.Should().Be("qms:manage");
        var headerFields = form.Fields!.Select(field => field.Field!).ToArray();
        headerFields.Should().NotContain(field => field.Key == "inspectionId",
            "검사 ID는 서버가 생성하며 사용자가 입력하지 않는다");
        headerFields.Should().Contain(field =>
            field.Key == "lotId" && field.Required && field.OptionsQueryId == "QMS.InspectionLotCombo");
        headerFields.Should().Contain(field =>
            field.Key == "equipmentId" && field.Required && field.OptionsQueryId == "QMS.InspectionEquipmentCombo");
        headerFields.Should().Contain(field =>
            field.Key == "samplingPlanRevisionId" && field.OptionsQueryId == "QMS.SamplingPlanRevisionCombo");
        headerFields.Should().Contain(field =>
            field.Key == "relationType"
            && field.Options!.SequenceEqual(new[] { "Original", "Correction", "Reinspection" }));
        headerFields.Should().Contain(field => field.Key == "parentInspectionId" && !field.Required);
        headerFields.Should().Contain(field =>
            field.Key == "idempotencyKey" && field.Required && field.Hidden
            && field.ValueGenerator == FieldValueGenerator.UuidV4);

        var collection = nodes.OfType<CollectionWidget>().Should().ContainSingle().Subject;
        collection.CollectionKey.Should().Be("items");
        collection.MinItems.Should().Be(1);
        collection.RequiredPermission.Should().Be("qms:manage");
        var itemFields = collection.Fields!.Select(field => field.Field!).ToArray();
        itemFields.Should().Contain(field =>
            field.Key == "specId" && field.Required && field.OptionsQueryId == "QMS.InspectionSpecCombo");
        itemFields.Should().Contain(field => field.Key == "measuredValue" && field.Type == FieldType.Number);
        itemFields.Should().Contain(field => field.Key == "attributeResult" && field.Type == FieldType.Select);
        itemFields.Should().Contain(field => field.Key == "sampleQuantity" && field.Required);
        itemFields.Should().Contain(field => field.Key == "defectQuantity" && field.Required);

        nodes.OfType<ButtonWidget>().Should().ContainSingle(button =>
            button.Command == commandId && button.RequiredPermission == "qms:manage");
        var grid = nodes.OfType<GridWidget>().Should().ContainSingle().Subject;
        grid.RequiredPermission.Should().Be("qms:read");
        grid.Columns!.Select(column => column.Key).Should().Contain(
        [
            "RESULT_ID", "ITEM_SEQUENCE", "LOT_QTY", "SAMPLE_QTY", "DEFECT_QTY",
            "ITEM_SAMPLE_QTY", "ITEM_DEFECT_QTY", "EFFECTIVE_RESULT", "IS_CANCELLED", "IS_SUPERSEDED",
        ]);
        ScreenDefinitionCapabilityValidator.Validate(definition).Should().BeEmpty();
    }

    [Theory]
    [InlineData("QMS_INSP_IMPORT_REGISTRATION_HIST", ScreenPurpose.Inquiry)]
    [InlineData("QMS_INSP_PROCESS_REGISTRATION_HIST", ScreenPurpose.Inquiry)]
    [InlineData("QMS_INSP_SHIPPING_REGISTRATION_HIST", ScreenPurpose.Inquiry)]
    [InlineData("QMS_REP_IMPORT_STATUS", ScreenPurpose.Report)]
    [InlineData("QMS_REP_PROCESS_STATUS", ScreenPurpose.Report)]
    [InlineData("QMS_REP_SHIPPING_STATUS", ScreenPurpose.Report)]
    [InlineData("FACTORY_QCA_REPORT_IMPORT_INSPECTION_STATUS", ScreenPurpose.Report)]
    [InlineData("FACTORY_QCA_REPORT_SEGMENT_INSPECTION_STATUS", ScreenPurpose.Report)]
    [InlineData("FACTORY_QCA_REPORT_DELIVERY_INSPECTION_STATUS", ScreenPurpose.Report)]
    public void Qms_history_and_status_screens_are_structurally_read_only(
        string uiId,
        ScreenPurpose expectedPurpose)
    {
        var definition = new InMemoryScreenDefinitionProvider().Get(uiId);

        definition.Should().NotBeNull();
        definition!.Purpose.Should().Be(expectedPurpose);
        definition.Fields.Should().BeEmpty();
        definition.SaveQueryId.Should().BeNull();
        definition.DeleteQueryId.Should().BeNull();
        definition.BulkCommands.Should().BeNullOrEmpty();
        definition.Columns.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Sales_order_management_exposes_draft_editor_with_master_data_options_and_due_date()
    {
        var definition = new InMemoryScreenDefinitionProvider().Get("FACTORY_SLS_SALES_ORDER");

        definition.Should().NotBeNull();
        definition!.Title.Should().Be("수주 관리");
        definition!.Purpose.Should().Be(ScreenPurpose.Manage);
        definition.SaveQueryId.Should().Be("SLS.CreateSalesOrder");
        definition.DeleteQueryId.Should().Be("SLS.DeleteSalesOrder");
        definition.Fields.Should().Contain(field =>
            field.Key == "plantId" && field.Required && field.OptionsQueryId == "MDM.PlantCombo");
        definition.Fields.Should().Contain(field =>
            field.Key == "customerId" && field.Required && field.OptionsQueryId == "MDM.CustomerCombo");
        definition.Fields.Should().Contain(field =>
            field.Key == "productId" && field.Required && field.OptionsQueryId == "MDM.ProductCombo");
        definition.Fields.Should().Contain(field =>
            field.Key == "planEndDate" && field.Type == FieldType.Date && field.Required);
        definition.Fields.Should().Contain(field =>
            field.Key == "planQty" && field.Type == FieldType.Number && field.Required);
        definition.Fields.Should().Contain(field =>
            field.Key == "salesOrderId" && field.Label == "수주 번호");
        definition.Fields.Should().Contain(field =>
            field.Key == "salesOrderName" && field.Label == "수주명");
        definition.Columns.Should().Contain(column =>
            column.Key == "SALES_ORDER_ID" && column.Caption == "수주 번호");
        definition.Columns.Should().Contain(column =>
            column.Key == "SALES_ORDER_NAME" && column.Caption == "수주명");
        definition.Columns!.Select(column => column.Key).Take(7).Should().Equal(
            "SALES_ORDER_ID", "SALES_ORDER_NAME", "CUSTOMER_ID", "PRODUCT_ID",
            "PLAN_END_DATE", "STATUS", "PLAN_QTY");
        definition.SearchFields.Should().Contain(field => field.Key == "status");
    }

    [Theory]
    [InlineData("FACTORY_SLS_SALES_REQUEST", "판매 요청")]
    [InlineData("FACTORY_SLS_REPORT_DELIVERY", "출하 현황")]
    public void Related_sales_screens_use_the_same_business_term_as_their_menu(
        string uiId,
        string expectedTitle)
    {
        var definition = new InMemoryScreenDefinitionProvider().Get(uiId);

        definition.Should().NotBeNull();
        definition!.Title.Should().Be(expectedTitle);
        if (uiId == "FACTORY_SLS_SALES_REQUEST")
            definition.Columns.Should().Contain(column =>
                column.Key == "SALES_ORDER_ID" && column.Caption == "수주 번호");
    }

    [Theory]
    [InlineData("EPT_STD_TAKT_TARGET")]
    [InlineData("FACTORY_COM_ACTION_DEF")]
    [InlineData("FACTORY_STD_BOR_CONDITION")]
    [InlineData("FACTORY_STD_ITEM_PLANNING")]
    [InlineData("FACTORY_STD_LABEL_MAPPING_MANAGEMENT")]
    [InlineData("FACTORY_STD_LABEL_MASTER")]
    [InlineData("FACTORY_STD_ROUTING_STEP")]
    [InlineData("FACTORY_STD_UOM")]
    [InlineData("FACTORY_STD_WORK_CENTER")]
    [InlineData("MES_MDM_COM_SHIFT")]
    [InlineData("MES_MDM_COM_VENDOR")]
    [InlineData("MES_MDM_COM_VENDOR_ITEM")]
    [InlineData("MICUBE_STANDARD_EQUIPMENT_EVENT")]
    [InlineData("MICUBE_STANDARD_EQUIPMENT_STATE_ALARM_MAPPING")]
    [InlineData("MICUBE_STANDARD_EQUIPMENT_STATE_EVENT_MAPPING")]
    [InlineData("MICUBE_STANDARD_MAIL_SERVER")]
    [InlineData("MICUBE_STANDARD_USER_EQUIPMENT_ALARM_MAIL_MAP")]
    public void Simple_crud_master_screens_are_explicit_manage(string uiId)
    {
        var definition = new InMemoryScreenDefinitionProvider().Get(uiId);

        definition.Should().NotBeNull();
        definition!.Purpose.Should().Be(ScreenPurpose.Manage);

        var capabilities = ScreenDefinitionCapabilityValidator.Inspect(definition);
        capabilities.HasEditableInput.Should().BeTrue();
        capabilities.HasSavePath.Should().BeTrue();
        capabilities.HasDeletePath.Should().BeTrue();
        ScreenDefinitionCapabilityValidator.Validate(definition).Should().BeEmpty();
    }

    [Fact]
    public void Routing_step_requires_process_mapping_for_serial_work_order_route_expansion()
    {
        var definition = new InMemoryScreenDefinitionProvider().Get("FACTORY_STD_ROUTING_STEP");

        definition.Should().NotBeNull();
        definition!.Fields.Should().ContainSingle(field => field.Key == "processId")
            .Which.Required.Should().BeTrue();
        definition.Columns.Should().ContainSingle(column => column.Key == "PROCESS_ID");
        definition.SaveQueryId.Should().Be("MDM.CreateRoutingStep");
    }

    [Theory]
    [InlineData("POC_PPM_WORK_ORDER")]
    [InlineData("FACTORY_PPM_WORK_ORDER")]
    public void Work_order_management_registers_through_typed_bridge_with_routing_scope(string uiId)
    {
        var definition = new InMemoryScreenDefinitionProvider().Get(uiId);

        definition.Should().NotBeNull();
        definition!.Purpose.Should().Be(ScreenPurpose.Manage);
        definition.SaveQueryId.Should().Be(PomWorkOrderMetaCommands.Create);
        definition.SaveRequiredPermission.Should().Be("pom:manage");
        definition.Fields.Should().ContainSingle(field => field.Key == "routingScope")
            .Which.Options.Should().Equal("Unbound", "Operation", "SerialRoute");
        definition.Columns.Should().ContainSingle(column => column.Key == "ROUTING_SCOPE");
    }

    [Fact]
    public void Factory_work_order_management_exposes_release_and_cancel_as_guarded_bulk_commands()
    {
        var definition = new InMemoryScreenDefinitionProvider().Get("FACTORY_PPM_WORK_ORDER");

        definition.Should().NotBeNull();
        definition!.BulkCommands.Should().HaveCount(2);
        definition.BulkCommands.Should().ContainSingle(command =>
            command.CommandQueryId == PomWorkOrderMetaCommands.Release
            && command.RequiredPermission == "pom:manage"
            && command.ConfirmMessage != null);
        definition.BulkCommands.Should().ContainSingle(command =>
            command.CommandQueryId == PomWorkOrderMetaCommands.Cancel
            && command.RequiredPermission == "pom:manage"
            && command.ConfirmMessage != null);
    }

    [Theory]
    [InlineData("DEMO_GRID")]
    [InlineData("DEMO_QMS_DEFECT_CLASS")]
    [InlineData("EES_EPT_ALARM_HISTORY")]
    [InlineData("EES_EPT_EQUIPMENT_ALARM_HISTORY")]
    [InlineData("EES_EPT_EQUIPMENT_EVENT_HISTORY")]
    [InlineData("EES_EPT_EQUIPMENT_PRODUCTIVE_HISTORY")]
    [InlineData("EES_EPT_EQUIPMENT_STATE_HISTORY")]
    [InlineData("EES_EPT_INTERESTED_INDEX_VIEW")]
    [InlineData("EES_FDC_INTERLOCK_HISTORY")]
    [InlineData("EES_FDC_VIRTUAL_EVENT_HISTORY")]
    [InlineData("FACTORY_EMS_PM_ORDER_RESULT_LIST")]
    [InlineData("FACTORY_EMS_STD_SPARE_PART_INOUT_HISTORY")]
    [InlineData("FACTORY_EMS_STD_SPARE_PART_STOCK")]
    [InlineData("FACTORY_RPT_LOT_TRACE")]
    [InlineData("FACTORY_STD_LABEL_ISSUE_HISTORY")]
    [InlineData("LOG_VIEWER")]
    [InlineData("POC_LOT_TRACE")]
    [InlineData("POC_LOT_TRACE_TREE")]
    [InlineData("QMS_CLM_STATUS_VIEW")]
    [InlineData("QMS_INSP_LONGTERM_HISTORY")]
    [InlineData("QMS_INSP_LONGTERM_PRODUCT_INSP_HISTORY")]
    [InlineData("QMS_SPM_EVL_RESULT_COMPARISON")]
    [InlineData("QMS_SPM_EVL_RESULT_VIEW")]
    [InlineData("SYSTEM2_MONITOR_REQLOG")]
    public void Read_only_inquiry_batch_has_an_explicit_safe_contract(string uiId)
        => AssertReadOnlyPurpose(uiId, ScreenPurpose.Inquiry);

    [Theory]
    [InlineData("DASHBOARD_SUMMARY")]
    [InlineData("EES_EPT_EQUIPMENT_LOSS_ANALYSIS")]
    [InlineData("EES_EPT_EQUIPMENT_STATE_STATUS")]
    [InlineData("EES_EPT_OVERALL_EQUIPMENT_EFFECTIVENESS")]
    [InlineData("EES_EPT_PLANT_MONITORING")]
    [InlineData("EES_EPT_TAKT_TIME")]
    [InlineData("EES_EPT_WORST10_ALARM")]
    [InlineData("EES_EPT_WORST5_LOSS")]
    [InlineData("EES_FDC_DATA_CHART")]
    [InlineData("EES_FDC_INTERESTED_DATA_CHART")]
    [InlineData("EES_FDC_REAL_TIME_TRACE_PARA_MONITORING")]
    [InlineData("EES_FDC_SUMMARY_DATA_CHART")]
    [InlineData("EES_FDC_TOOL_TO_TOOL_MATCHING")]
    [InlineData("EES_POPUP_MONITORING_DASHBOARD")]
    [InlineData("FACTORY_DASHBOARD_MENU_PRODUCTIVITY")]
    [InlineData("FACTORY_DASHBOARD_MENU_SAMPLE_TEST")]
    [InlineData("FACTORY_DLV_REPORT_DELIVERYORDER")]
    [InlineData("FACTORY_PPM_REPORT_PRODUCTIONORDER")]
    [InlineData("FACTORY_PPM_REPORT_WORKORDER")]
    [InlineData("FACTORY_PRC_REPORT_MOVEORDER")]
    [InlineData("FACTORY_PRC_REPORT_PURCHASEORDER")]
    [InlineData("FACTORY_SLS_REPORT_DELIVERY")]
    [InlineData("FACTORY_WPM_REPORT_CONSUME_MATERIAL_LOT")]
    [InlineData("FACTORY_WPM_REPORT_MATERIAL_DISPENSING_ORDER")]
    [InlineData("FACTORY_WPM_REPORT_YIELD_STATUS")]
    [InlineData("NX_CRP_LOAD")]
    [InlineData("POC_FDC_DATA_CHART")]
    [InlineData("POC_INPROCESS_LOT")]
    [InlineData("POC_COATING_PROCESS")]
    [InlineData("POC_MIXING_PROCESS")]
    [InlineData("POC_ROLLING_PROCESS")]
    [InlineData("QMS_CLM_REPORT_ACTION_STATUS")]
    [InlineData("QMS_CLM_RPT_OCCUR_STATUS")]
    [InlineData("QMS_INSP_LONGTERM_PRODUCT_REQUEST")]
    [InlineData("QMS_INSP_LONGTERM_REQUEST")]
    [InlineData("QMS_MEASURE_INSTRUMENT_REPORT")]
    [InlineData("QMS_MEQ_CALIBRATION_STATUS")]
    [InlineData("QMS_MEQ_MEASURE_FAILURE_RATE")]
    [InlineData("QMS_MEQ_MEASURE_REPAIR_DETAILS")]
    [InlineData("QMS_QCA_NCR_OVERVIEW")]
    [InlineData("QMS_QCA_PENDING_STATUS")]
    [InlineData("QMS_REP_CHANGE_STATUS")]
    [InlineData("QMS_REP_ITEM_STATUS")]
    [InlineData("QMS_REP_NCR_STATUS")]
    [InlineData("QMS_SPC_CONTROL_CHART")]
    [InlineData("QMS_SPM_EVL_REPORT")]
    [InlineData("SYS_MENU_USAGE_STATS")]
    public void Read_only_report_batch_has_an_explicit_safe_contract(string uiId)
        => AssertReadOnlyPurpose(uiId, ScreenPurpose.Report);

    [Theory]
    [InlineData("DEMO_PLANT_FORM", ScreenPurpose.Register)]
    [InlineData("DEMO_LAYOUT", ScreenPurpose.Manage)]
    public void Demo_editors_are_explicit_only_when_the_active_surface_can_save(
        string uiId,
        ScreenPurpose expectedPurpose)
    {
        var definition = new InMemoryScreenDefinitionProvider().Get(uiId);

        definition.Should().NotBeNull();
        definition!.Purpose.Should().Be(expectedPurpose);
        var capabilities = ScreenDefinitionCapabilityValidator.Inspect(definition);
        capabilities.HasEditableInput.Should().BeTrue();
        capabilities.HasSavePath.Should().BeTrue();
        ScreenDefinitionCapabilityValidator.Validate(definition).Should().BeEmpty();
    }

    [Fact]
    public void Mrp_planning_is_execute_only_with_the_registered_host_aggregate_contract()
    {
        var definition = new InMemoryScreenDefinitionProvider().Get("NX_MRP_PLANNING");
        var catalog = new MetaCommandDriverCatalog([new MrpConversionMetaCommandDriver()]);

        definition.Should().NotBeNull();
        definition!.Purpose.Should().Be(ScreenPurpose.Execute);
        definition.BulkCommands.Should().ContainSingle(command =>
            command.CommandQueryId == MrpConversionMetaCommands.Convert);
        catalog.TryGetDescriptor(MrpConversionMetaCommands.Convert, out var descriptor).Should().BeTrue();
        descriptor.Should().NotBeNull();
        descriptor!.ExecutionMode.Should().Be(MetaCommandExecutionMode.HostRequiredAggregate);
        descriptor.Effect.Should().Be(MetaCommandEffect.Mutating);

        var capabilities = ScreenDefinitionCapabilityValidator.Inspect(definition, catalog);
        capabilities.HasReadPath.Should().BeTrue();
        capabilities.HasBulkMutationPath.Should().BeTrue();
        ScreenDefinitionCapabilityValidator.Validate(definition, catalog).Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_purpose_batches_have_the_expected_distribution()
    {
        var provider = new InMemoryScreenDefinitionProvider();
        var ids = await provider.GetKnownUiIdsAsync();
        var definitions = ids
            .Select(provider.Get)
            .Where(definition => definition is not null)
            .Cast<ScreenDefinition>()
            .DistinctBy(definition => definition.UiId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ids.Should().HaveCount(283,
            "코드 화면 270개와 URL 호환용 legacy alias 13개를 모두 카탈로그 키로 유지해야 한다");
        definitions.Should().HaveCount(270,
            "legacy alias는 원본 ScreenDefinition을 공유하므로 canonical 화면 수에는 중복되면 안 된다");

        definitions.Count(definition => definition.Purpose == ScreenPurpose.Auto).Should().Be(2);
        definitions.Count(definition => definition.Purpose == ScreenPurpose.Inquiry).Should().Be(177);
        definitions.Count(definition => definition.Purpose == ScreenPurpose.Report).Should().Be(53);
        definitions.Count(definition => definition.Purpose == ScreenPurpose.Manage).Should().Be(29);
        definitions.Count(definition => definition.Purpose == ScreenPurpose.Register).Should().Be(8);
        definitions.Count(definition => definition.Purpose == ScreenPurpose.Execute).Should().Be(1);
    }

    private static void AssertReadOnlyPurpose(string uiId, ScreenPurpose expectedPurpose)
    {
        var definition = new InMemoryScreenDefinitionProvider().Get(uiId);

        definition.Should().NotBeNull();
        definition!.Purpose.Should().Be(expectedPurpose);
        HasReadPath(definition).Should().BeTrue();

        var capabilities = ScreenDefinitionCapabilityValidator.Inspect(definition);
        capabilities.HasEditableInput.Should().BeFalse();
        capabilities.HasSavePath.Should().BeFalse();
        capabilities.HasDeletePath.Should().BeFalse();
        capabilities.HasBulkMutationPath.Should().BeFalse();
        capabilities.HasLayoutCommandPath.Should().BeFalse();
        capabilities.HasRegistrationWritePath.Should().BeFalse();
        capabilities.HasAnyWritePath.Should().BeFalse();
        ScreenDefinitionCapabilityValidator.Validate(definition).Should().BeEmpty();
    }

    private static bool HasReadPath(ScreenDefinition definition)
        => !string.IsNullOrWhiteSpace(definition.QueryId)
            || !string.IsNullOrWhiteSpace(definition.CountQueryId)
            || HasLayoutReadPath(definition.Layout);

    private static bool HasLayoutReadPath(LayoutNode? node)
        => node switch
        {
            GridWidget grid => !string.IsNullOrWhiteSpace(grid.QueryId),
            KpiWidget kpi => !string.IsNullOrWhiteSpace(kpi.QueryId),
            BadgeWidget badge => !string.IsNullOrWhiteSpace(badge.QueryId),
            TrendChartWidget chart => !string.IsNullOrWhiteSpace(chart.QueryId),
            SectionNode section => section.Children?.Any(HasLayoutReadPath) == true,
            RowNode row => row.Children?.Any(HasLayoutReadPath) == true,
            ColumnNode column => column.Children?.Any(HasLayoutReadPath) == true,
            _ => false,
        };

    private static IEnumerable<LayoutNode> AllLayoutNodes(LayoutNode node)
    {
        yield return node;
        var children = node switch
        {
            SectionNode section => section.Children,
            RowNode row => row.Children,
            ColumnNode column => column.Children,
            _ => null,
        };
        foreach (var child in children ?? [])
        foreach (var descendant in AllLayoutNodes(child))
            yield return descendant;
    }
}

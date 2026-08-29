using NexaOne.Web.Services.Meta;
using Xunit.Abstractions;

namespace NexaOne.UnitTests.Web;

public sealed class ScreenDefinitionCapabilityValidatorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(ScreenPurpose.Register)]
    [InlineData(ScreenPurpose.Manage)]
    public void Editable_purpose_requires_an_editable_input(ScreenPurpose purpose)
    {
        var definition = new ScreenDefinition(
            "EDITABLE_WITHOUT_INPUT",
            "입력 없는 편집 화면",
            [new FieldDefinition("id", "ID", ReadOnly: true)],
            SaveQueryId: "MDM.Save",
            Purpose: purpose);

        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionCapabilityValidator.EditablePurposeMissingInput
            && item.Severity == ScreenCapabilityDiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(ScreenPurpose.Register)]
    [InlineData(ScreenPurpose.Manage)]
    public void Editable_purpose_requires_a_write_path(ScreenPurpose purpose)
    {
        var definition = new ScreenDefinition(
            "EDITABLE_WITHOUT_WRITE",
            "쓰기 없는 편집 화면",
            [new FieldDefinition("name", "이름")],
            Purpose: purpose);

        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionCapabilityValidator.EditablePurposeMissingWritePath
            && item.Severity == ScreenCapabilityDiagnosticSeverity.Error);
    }

    [Fact]
    public void Register_does_not_treat_delete_only_as_a_registration_path()
    {
        var definition = new ScreenDefinition(
            "DELETE_ONLY_REGISTER",
            "삭제 전용 등록 화면",
            [new FieldDefinition("id", "ID")],
            DeleteQueryId: "MDM.Delete",
            Purpose: ScreenPurpose.Register);

        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionCapabilityValidator.EditablePurposeMissingWritePath);
    }

    [Theory]
    [InlineData(ScreenPurpose.Inquiry)]
    [InlineData(ScreenPurpose.Report)]
    public void Read_only_purpose_rejects_every_mutation_surface(ScreenPurpose purpose)
    {
        var definition = new ScreenDefinition(
            "READ_ONLY_WITH_MUTATIONS",
            "변경 경로가 섞인 조회 화면",
            Array.Empty<FieldDefinition>(),
            QueryId: "MDM.List",
            SaveQueryId: "MDM.Save",
            DeleteQueryId: "MDM.Delete",
            BulkCommands: [new BulkCommandDefinition("상태 변경", "MDM.ChangeStatus")],
            Layout: new RowNode
            {
                Children =
                [
                    new GridWidget { QueryId = "MDM.List" },
                    new FormWidget { SaveQueryId = "MDM.SaveNested" },
                    new ButtonWidget { Label = "변경", Command = "MDM.Change" },
                ],
            },
            Purpose: purpose);

        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        diagnostics.Select(item => item.Code).Should().BeEquivalentTo(
        [
            ScreenDefinitionCapabilityValidator.ReadOnlyPurposeHasSavePath,
            ScreenDefinitionCapabilityValidator.ReadOnlyPurposeHasDeletePath,
            ScreenDefinitionCapabilityValidator.ReadOnlyPurposeHasBulkMutation,
            ScreenDefinitionCapabilityValidator.ReadOnlyPurposeHasLayoutCommand,
        ]);
        diagnostics.Should().OnlyContain(item => item.Severity == ScreenCapabilityDiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(ScreenPurpose.Inquiry)]
    [InlineData(ScreenPurpose.Report)]
    public void Read_only_purpose_allows_declared_non_mutating_commands(ScreenPurpose purpose)
    {
        const string exportCommand = "bridge:demo.export";
        var definition = new ScreenDefinition(
            "READ_ONLY_EXPORT",
            "내보내기가 있는 조회 화면",
            Array.Empty<FieldDefinition>(),
            BulkCommands: [new BulkCommandDefinition("선택 내보내기", exportCommand)],
            Layout: new RowNode
            {
                Children =
                [
                    new GridWidget { QueryId = "DEMO.List" },
                    new ButtonWidget { Label = "CSV 내보내기", Command = exportCommand },
                ],
            },
            Purpose: purpose);
        var descriptor = new MetaCommandDescriptor(
            exportCommand,
            RequiredPermission: "demo:read",
            ExecutionMode: MetaCommandExecutionMode.PerRow,
            Effect: MetaCommandEffect.NonMutating);
        var catalog = new MetaCommandDriverCatalog([new DescriptorOnlyMetaCommandDriver(descriptor)]);

        var snapshot = ScreenDefinitionCapabilityValidator.Inspect(definition, catalog);
        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition, catalog);

        snapshot.HasBulkMutationPath.Should().BeFalse();
        snapshot.HasLayoutCommandPath.Should().BeFalse();
        snapshot.HasNonMutatingCommandPath.Should().BeTrue();
        snapshot.HasAnyWritePath.Should().BeFalse();
        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Execute_is_not_satisfied_by_a_non_mutating_export_only()
    {
        const string exportCommand = "bridge:demo.export";
        var definition = new ScreenDefinition(
            "EXPORT_ONLY_EXECUTE",
            "내보내기만 있는 실행 화면",
            Array.Empty<FieldDefinition>(),
            Layout: new ButtonWidget { Label = "CSV 내보내기", Command = exportCommand },
            Purpose: ScreenPurpose.Execute);
        var descriptor = new MetaCommandDescriptor(
            exportCommand,
            RequiredPermission: "demo:read",
            Effect: MetaCommandEffect.NonMutating);
        var catalog = new MetaCommandDriverCatalog([new DescriptorOnlyMetaCommandDriver(descriptor)]);

        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition, catalog);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionCapabilityValidator.ExecutePurposeMissingExecutionPath);
    }

    [Fact]
    public void Inspect_separates_primary_data_read_from_count_and_option_context_reads()
    {
        var definition = new ScreenDefinition(
            "READ_BINDINGS",
            "조회 binding",
            [new FieldDefinition("itemId", "품목", FieldType.Select, OptionsQueryId: "MDM.ItemCombo")],
            Columns: [new GridColumnDefinition("ITEM_ID", "품목")],
            QueryId: "MDM.ItemList",
            SearchFields:
            [
                new FieldDefinition("plantId", "공장", FieldType.Select, OptionsQueryId: "MDM.PlantCombo"),
            ],
            CountQueryId: "MDM.ItemCount");

        var snapshot = ScreenDefinitionCapabilityValidator.Inspect(definition);

        snapshot.HasReadPath.Should().BeTrue("본문 QueryId는 primary read binding이다");
        snapshot.HasContextualReadPath.Should().BeTrue("CountQueryId와 OptionsQueryId는 보조 조회 binding이다");
        snapshot.HasAnyReadPath.Should().BeTrue();
    }

    [Fact]
    public void Flat_query_and_count_without_columns_are_not_active_read_bindings()
    {
        var definition = new ScreenDefinition(
            "QUERY_WITHOUT_COLUMNS",
            "컬럼 없는 조회",
            Array.Empty<FieldDefinition>(),
            QueryId: "MDM.List",
            CountQueryId: "MDM.Count",
            Purpose: ScreenPurpose.Inquiry);

        var snapshot = ScreenDefinitionCapabilityValidator.Inspect(definition);
        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        snapshot.HasReadPath.Should().BeFalse();
        snapshot.HasContextualReadPath.Should().BeFalse("CountQueryId도 활성 그리드 조회가 있을 때만 실행된다");
        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionCapabilityValidator.ReadOnlyPurposeMissingReadPath);
    }

    [Fact]
    public void Layout_mode_ignores_hidden_flat_fields_query_count_options_and_save()
    {
        var definition = new ScreenDefinition(
            "HIDDEN_FLAT_SURFACES",
            "숨은 평면 surface",
            [new FieldDefinition("name", "이름", OptionsQueryId: "MDM.NameCombo")],
            Columns: [new GridColumnDefinition("NAME", "이름")],
            QueryId: "MDM.List",
            SaveQueryId: "MDM.Save",
            Layout: new TextWidget { Text = "레이아웃 모드" },
            CountQueryId: "MDM.Count",
            Purpose: ScreenPurpose.Manage);

        var snapshot = ScreenDefinitionCapabilityValidator.Inspect(definition);
        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        snapshot.HasEditableInput.Should().BeFalse();
        snapshot.HasReadPath.Should().BeFalse();
        snapshot.HasContextualReadPath.Should().BeFalse();
        snapshot.HasSavePath.Should().BeFalse();
        diagnostics.Select(item => item.Code).Should().BeEquivalentTo(
        [
            ScreenDefinitionCapabilityValidator.EditablePurposeMissingInput,
            ScreenDefinitionCapabilityValidator.EditablePurposeMissingWritePath,
        ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Search_field_option_query_is_active_in_flat_and_layout_modes(bool useLayout)
    {
        var definition = new ScreenDefinition(
            "SEARCH_OPTIONS",
            "검색 옵션",
            Array.Empty<FieldDefinition>(),
            Layout: useLayout ? new TextWidget { Text = "레이아웃" } : null,
            SearchFields:
            [
                new FieldDefinition("plantId", "공장", FieldType.Select, OptionsQueryId: "MDM.PlantCombo"),
            ]);

        ScreenDefinitionCapabilityValidator.Inspect(definition)
            .HasContextualReadPath.Should().BeTrue();
    }

    [Fact]
    public void Flat_save_without_fields_is_not_an_active_execution_path()
    {
        var definition = new ScreenDefinition(
            "SAVE_WITHOUT_FIELDS",
            "필드 없는 저장",
            Array.Empty<FieldDefinition>(),
            SaveQueryId: "MDM.Save",
            Purpose: ScreenPurpose.Execute);

        ScreenDefinitionCapabilityValidator.Validate(definition).Should().ContainSingle(item =>
            item.Code == ScreenDefinitionCapabilityValidator.ExecutePurposeMissingExecutionPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_and_bulk_without_a_query_backed_grid_do_not_satisfy_execute(bool useLayout)
    {
        var definition = new ScreenDefinition(
            "MUTATION_WITHOUT_GRID",
            "조회 없는 그리드 행 명령",
            Array.Empty<FieldDefinition>(),
            Columns: useLayout ? null : [new GridColumnDefinition("ID", "ID")],
            Layout: useLayout ? new GridWidget() : null,
            DeleteQueryId: "MDM.Delete",
            BulkCommands: [new BulkCommandDefinition("전이", "MDM.Transition")],
            Purpose: ScreenPurpose.Execute);

        var snapshot = ScreenDefinitionCapabilityValidator.Inspect(definition);
        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        snapshot.HasDeletePath.Should().BeFalse();
        snapshot.HasBulkMutationPath.Should().BeFalse();
        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionCapabilityValidator.ExecutePurposeMissingExecutionPath);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Layout_grid_activates_top_level_delete_and_bulk_for_execute(
        bool hasDelete,
        bool hasBulk)
    {
        var definition = new ScreenDefinition(
            "LAYOUT_GRID_MUTATION",
            "레이아웃 그리드 행 명령",
            Array.Empty<FieldDefinition>(),
            Layout: new GridWidget { QueryId = "MDM.List" },
            DeleteQueryId: hasDelete ? "MDM.Delete" : null,
            BulkCommands: hasBulk ? [new BulkCommandDefinition("전이", "MDM.Transition")] : null,
            Purpose: ScreenPurpose.Execute);

        var snapshot = ScreenDefinitionCapabilityValidator.Inspect(definition);

        snapshot.HasDeletePath.Should().Be(hasDelete);
        snapshot.HasBulkMutationPath.Should().Be(hasBulk);
        ScreenDefinitionCapabilityValidator.Validate(definition).Should().BeEmpty();
    }

    [Fact]
    public void Layout_grid_can_override_inherited_bulk_commands_per_surface()
    {
        var command = "bridge:pom.work-scope.start";
        var managed = new ScreenDefinition(
            "WORK_SCOPE_GRID",
            "작업 범위",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children =
                [
                    new GridWidget
                    {
                        QueryId = "POM.WorkScopeList",
                        BulkCommands = [new BulkCommandDefinition("시작", command, RequiredPermission: "pom:execute")],
                    },
                ],
            },
            Purpose: ScreenPurpose.Execute);

        var readOnlyAuxiliary = managed with
        {
            UiId = "WORK_SCOPE_AUXILIARY",
            Purpose = ScreenPurpose.Inquiry,
            Layout = new GridWidget
            {
                QueryId = "EMS.ToolUsageHistoryList",
                BulkCommands = Array.Empty<BulkCommandDefinition>(),
            },
        };

        var managedSnapshot = ScreenDefinitionCapabilityValidator.Inspect(managed);
        var auxiliarySnapshot = ScreenDefinitionCapabilityValidator.Inspect(readOnlyAuxiliary);

        managedSnapshot.HasBulkMutationPath.Should().BeTrue();
        ScreenDefinitionCapabilityValidator.Validate(managed).Should().BeEmpty();
        auxiliarySnapshot.HasReadPath.Should().BeTrue();
        auxiliarySnapshot.HasBulkMutationPath.Should().BeFalse(
            "빈 그리드 명령 목록은 화면 전역 상태전이의 상속을 차단해야 한다");
        ScreenDefinitionCapabilityValidator.Validate(readOnlyAuxiliary).Should().BeEmpty();
    }

    [Fact]
    public void Every_layout_data_widget_query_is_a_primary_read_binding()
    {
        LayoutNode[] widgets =
        [
            new GridWidget { QueryId = "MDM.GridList" },
            new KpiWidget { Label = "KPI", QueryId = "MDM.Kpi" },
            new BadgeWidget { Label = "상태", QueryId = "MDM.Status" },
            new TrendChartWidget { Label = "추세", QueryId = "MDM.Trend" },
        ];

        foreach (var widget in widgets)
        {
            var definition = new ScreenDefinition(
                $"LAYOUT_{widget.GetType().Name}",
                "레이아웃 조회",
                Array.Empty<FieldDefinition>(),
                Layout: new SectionNode { Children = [widget] });

            var snapshot = ScreenDefinitionCapabilityValidator.Inspect(definition);

            snapshot.HasReadPath.Should().BeTrue($"{widget.GetType().Name} QueryId는 실제 본문 조회를 실행한다");
        }
    }

    [Fact]
    public void Layout_form_field_option_query_is_contextual_but_not_primary_read()
    {
        var definition = new ScreenDefinition(
            "LAYOUT_OPTION",
            "레이아웃 선택 옵션",
            Array.Empty<FieldDefinition>(),
            Layout: new FormWidget
            {
                Fields =
                [
                    new FieldWidget
                    {
                        Field = new FieldDefinition(
                            "equipmentId",
                            "설비",
                            FieldType.Select,
                            OptionsQueryId: "MDM.EquipmentCombo"),
                    },
                ],
            });

        var snapshot = ScreenDefinitionCapabilityValidator.Inspect(definition);

        snapshot.HasContextualReadPath.Should().BeTrue();
        snapshot.HasReadPath.Should().BeFalse("선택 옵션 조회만으로 화면 본문 데이터가 생기지는 않는다");
    }

    [Fact]
    public void Collection_fields_contribute_editable_input_and_option_query_capabilities()
    {
        var definition = new ScreenDefinition(
            "COLLECTION_REGISTER",
            "반복 입력 등록",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children =
                [
                    new FormWidget { SaveQueryId = "QMS.RecordInspection" },
                    new CollectionWidget
                    {
                        CollectionKey = "items",
                        Fields =
                        [
                            new FieldWidget
                            {
                                Field = new FieldDefinition(
                                    "specId",
                                    "검사 규격",
                                    FieldType.Select,
                                    OptionsQueryId: "QMS.InspectionSpecCombo"),
                            },
                        ],
                    },
                ],
            },
            Purpose: ScreenPurpose.Register);

        var snapshot = ScreenDefinitionCapabilityValidator.Inspect(definition);

        snapshot.HasEditableInput.Should().BeTrue();
        snapshot.HasContextualReadPath.Should().BeTrue();
        snapshot.HasSavePath.Should().BeTrue();
        ScreenDefinitionCapabilityValidator.Validate(definition).Should().BeEmpty();
    }

    [Fact]
    public void Hidden_generated_collection_field_is_not_an_editable_input()
    {
        var definition = new ScreenDefinition(
            "HIDDEN_COLLECTION_ONLY",
            "시스템 필드만 있는 반복 입력",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children =
                [
                    new FormWidget { SaveQueryId = "QMS.Save" },
                    new CollectionWidget
                    {
                        CollectionKey = "items",
                        Fields =
                        [
                            new FieldWidget
                            {
                                Field = new FieldDefinition(
                                    "rowKey",
                                    "행 키",
                                    Hidden: true,
                                    ValueGenerator: FieldValueGenerator.UuidV4),
                            },
                        ],
                    },
                ],
            },
            Purpose: ScreenPurpose.Register);

        ScreenDefinitionCapabilityValidator.Validate(definition).Should().ContainSingle(diagnostic =>
            diagnostic.Code == ScreenDefinitionCapabilityValidator.EditablePurposeMissingInput);
    }

    [Theory]
    [InlineData(ScreenPurpose.Inquiry)]
    [InlineData(ScreenPurpose.Report)]
    public void Read_only_purpose_requires_a_primary_read_even_with_count_and_options(ScreenPurpose purpose)
    {
        var definition = new ScreenDefinition(
            "CONTEXT_ONLY_READ",
            "보조 조회만 있는 화면",
            [new FieldDefinition("itemId", "품목", FieldType.Select, OptionsQueryId: "MDM.ItemCombo")],
            CountQueryId: "MDM.ItemCount",
            Purpose: purpose);

        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionCapabilityValidator.ReadOnlyPurposeMissingReadPath
            && item.Severity == ScreenCapabilityDiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(ScreenPurpose.Inquiry, false)]
    [InlineData(ScreenPurpose.Inquiry, true)]
    [InlineData(ScreenPurpose.Report, false)]
    [InlineData(ScreenPurpose.Report, true)]
    public void Read_only_purpose_rejects_flat_and_layout_editable_inputs(
        ScreenPurpose purpose,
        bool useLayout)
    {
        var definition = new ScreenDefinition(
            "READ_ONLY_EDITABLE",
            "편집 입력이 섞인 조회 화면",
            useLayout
                ? Array.Empty<FieldDefinition>()
                : [new FieldDefinition("name", "이름")],
            Columns: useLayout ? null : [new GridColumnDefinition("NAME", "이름")],
            QueryId: useLayout ? null : "MDM.List",
            Layout: useLayout
                ? new SectionNode
                {
                    Children =
                    [
                        new GridWidget { QueryId = "MDM.List" },
                        new FieldWidget { Field = new FieldDefinition("name", "이름") },
                    ],
                }
                : null,
            Purpose: purpose);

        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionCapabilityValidator.ReadOnlyPurposeHasEditableInput
            && item.Severity == ScreenCapabilityDiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(ScreenPurpose.Inquiry, false)]
    [InlineData(ScreenPurpose.Inquiry, true)]
    [InlineData(ScreenPurpose.Report, false)]
    [InlineData(ScreenPurpose.Report, true)]
    public void Read_only_purpose_allows_flat_and_layout_read_only_fields(
        ScreenPurpose purpose,
        bool useLayout)
    {
        var definition = new ScreenDefinition(
            "READ_ONLY_FIELD",
            "읽기 전용 필드 화면",
            useLayout
                ? Array.Empty<FieldDefinition>()
                : [new FieldDefinition("name", "이름", ReadOnly: true)],
            Columns: useLayout ? null : [new GridColumnDefinition("NAME", "이름")],
            QueryId: useLayout ? null : "MDM.List",
            Layout: useLayout
                ? new SectionNode
                {
                    Children =
                    [
                        new GridWidget { QueryId = "MDM.List" },
                        new FieldWidget { Field = new FieldDefinition("name", "이름", ReadOnly: true) },
                    ],
                }
                : null,
            Purpose: purpose);

        ScreenDefinitionCapabilityValidator.Validate(definition).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_layout_field_key_is_not_an_editable_input(string? fieldKey)
    {
        var definition = new ScreenDefinition(
            "EMPTY_FIELD_KEY",
            "빈 필드 키",
            Array.Empty<FieldDefinition>(),
            Layout: new FormWidget
            {
                SaveQueryId = "MDM.Save",
                Fields = [new FieldWidget { FieldKey = fieldKey }],
            },
            Purpose: ScreenPurpose.Register);

        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionCapabilityValidator.EditablePurposeMissingInput);
    }

    [Theory]
    [InlineData(ScreenPurpose.Register)]
    [InlineData(ScreenPurpose.Manage)]
    public void Editable_purpose_does_not_treat_delete_bulk_or_generic_command_as_create_update(ScreenPurpose purpose)
    {
        var definition = new ScreenDefinition(
            "TRANSITION_ONLY_EDIT",
            "전이 명령만 있는 편집 화면",
            [new FieldDefinition("id", "ID")],
            DeleteQueryId: "MDM.Delete",
            BulkCommands: [new BulkCommandDefinition("상태 변경", "MDM.ChangeStatus")],
            Layout: new SectionNode
            {
                Children =
                [
                    new GridWidget { QueryId = "MDM.List" },
                    new FieldWidget { FieldKey = "id" },
                    new ButtonWidget { Label = "승인", Command = "MDM.Approve" },
                ],
            },
            Purpose: purpose);

        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionCapabilityValidator.EditablePurposeMissingWritePath);
    }

    [Fact]
    public void Execute_requires_at_least_one_command_or_write_path()
    {
        var definition = new ScreenDefinition(
            "EMPTY_EXECUTION",
            "실행 경로 없는 작업 화면",
            [new FieldDefinition("workOrderId", "작업지시")],
            QueryId: "POM.WorkOrderList",
            BulkCommands: [new BulkCommandDefinition("빈 명령", "   ")],
            Layout: new ButtonWidget { Label = "빈 버튼", Command = "" },
            Purpose: ScreenPurpose.Execute);

        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionCapabilityValidator.ExecutePurposeMissingExecutionPath
            && item.Severity == ScreenCapabilityDiagnosticSeverity.Error);
    }

    [Fact]
    public void Layout_form_and_command_satisfy_registration_contract()
    {
        var definition = new ScreenDefinition(
            "LAYOUT_REGISTER",
            "레이아웃 등록",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children =
                [
                    new FormWidget
                    {
                        SaveQueryId = "bridge:sample.register",
                        Fields = [new FieldWidget { FieldKey = "value" }],
                    },
                    new ButtonWidget { Label = "등록", Command = "bridge:sample.register" },
                ],
            },
            Purpose: ScreenPurpose.Register);

        ScreenDefinitionCapabilityValidator.Validate(definition).Should().BeEmpty();
    }

    [Fact]
    public void Execute_keeps_existing_mixed_input_and_multi_command_template_compatible()
    {
        var definition = new ScreenDefinition(
            "WORK_EXECUTION",
            "작업 실행",
            Array.Empty<FieldDefinition>(),
            Layout: new RowNode
            {
                Children =
                [
                    new FormWidget
                    {
                        Fields =
                        [
                            new FieldWidget { Field = new FieldDefinition("workOrderId", "작업지시", ReadOnly: true) },
                            new FieldWidget { Field = new FieldDefinition("goodQty", "양품", FieldType.Number) },
                        ],
                    },
                    new ButtonWidget { Label = "시작", Command = "bridge:pom.work-order.start" },
                    new ButtonWidget { Label = "완료", Command = "bridge:pom.work-order.complete" },
                ],
            },
            Purpose: ScreenPurpose.Execute);

        ScreenDefinitionCapabilityValidator.Validate(definition).Should().BeEmpty();
    }

    [Fact]
    public void Auto_is_advisory_only_and_never_blocks_legacy_definition()
    {
        var definition = new ScreenDefinition(
            "LEGACY_AUTO",
            "기존 화면",
            Array.Empty<FieldDefinition>(),
            SaveQueryId: "LEGACY.Save");

        var diagnostics = ScreenDefinitionCapabilityValidator.Validate(definition);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Code.Should().Be(ScreenDefinitionCapabilityValidator.AutoPurposeAdvisory);
        diagnostics[0].Severity.Should().Be(ScreenCapabilityDiagnosticSeverity.Advisory);
    }

    [Fact]
    public async Task Every_seeded_explicit_purpose_satisfies_the_capability_contract()
    {
        var provider = new InMemoryScreenDefinitionProvider();
        var ids = await provider.GetKnownUiIdsAsync();
        var definitions = ids
            .Select(provider.Get)
            .Where(definition => definition is not null)
            .Cast<ScreenDefinition>()
            .DistinctBy(definition => definition.UiId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var diagnostics = ScreenDefinitionCapabilityValidator.Audit(definitions);
        var errors = diagnostics
            .Where(item => item.Severity == ScreenCapabilityDiagnosticSeverity.Error)
            .ToArray();
        var auto = diagnostics
            .Where(item => item.Code == ScreenDefinitionCapabilityValidator.AutoPurposeAdvisory)
            .OrderBy(item => item.UiId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        output.WriteLine($"Seed definitions: {definitions.Length}");
        output.WriteLine($"Explicit-purpose errors: {errors.Length}");
        output.WriteLine($"Auto-purpose advisories: {auto.Length}");
        output.WriteLine(string.Join(Environment.NewLine, auto.Select(item => item.UiId)));

        errors.Should().BeEmpty(string.Join(
            Environment.NewLine,
            errors.Select(item => $"{item.UiId} [{item.Code}] {item.Message}")));
        auto.Should().NotBeEmpty("기존 Auto 화면은 실패시키지 않고 마이그레이션 진단 목록으로 유지해야 한다");
    }

    /// <summary>실제 내보내기 도메인 행동을 만들지 않고 descriptor 전파만 검증하는 테스트 드라이버입니다.</summary>
    private sealed class DescriptorOnlyMetaCommandDriver(MetaCommandDescriptor descriptor) : IMetaCommandDriver
    {
        public IReadOnlyCollection<string> CommandIds { get; } = [descriptor.Id];
        public IReadOnlyCollection<MetaCommandDescriptor> Commands { get; } = [descriptor];

        public string? GetRequiredPermission(string commandId)
            => string.Equals(commandId, descriptor.Id, StringComparison.OrdinalIgnoreCase)
                ? descriptor.RequiredPermission
                : null;

        public MetaCommandAvailability CanExecute(
            string commandId,
            IReadOnlyDictionary<string, object?> parameters,
            MetaCommandExecutionContext context)
            => MetaCommandAvailability.Enabled;

        public Task<MetaCommandResult> ExecuteAsync(
            string commandId,
            IReadOnlyDictionary<string, object?> parameters,
            MetaCommandExecutionContext context,
            CancellationToken ct = default)
            => Task.FromResult(MetaCommandResult.Succeeded());
    }
}

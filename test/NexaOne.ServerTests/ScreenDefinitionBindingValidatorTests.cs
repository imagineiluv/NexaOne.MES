using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Application.Query;
using NexaOne.Server.Gateway;
using NexaOne.Web.Services.Meta;
using Xunit;
using Xunit.Abstractions;

namespace NexaOne.ServerTests;

public sealed class ScreenDefinitionBindingValidatorTests :
    IClassFixture<ScreenDefinitionSeedControllerTests.ScreenSeedFactory>
{
    private readonly ScreenDefinitionSeedControllerTests.ScreenSeedFactory _factory;
    private readonly ITestOutputHelper _output;

    public ScreenDefinitionBindingValidatorTests(
        ScreenDefinitionSeedControllerTests.ScreenSeedFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public void Unknown_and_wrong_kind_bindings_are_reported_by_context()
    {
        var validator = Validator(
            queries:
            [
                Read("DEMO.Read", "demo:read"),
                Write("DEMO.Write", "demo:manage"),
            ]);
        var definition = new ScreenDefinition(
            "BROKEN_KINDS",
            "Broken bindings",
            [new FieldDefinition("value", "Value")],
            Columns: [new GridColumnDefinition("ID", "ID")],
            QueryId: "DEMO.MissingRead",
            SaveQueryId: "DEMO.Read",
            CountQueryId: "DEMO.Write",
            DeleteQueryId: "DEMO.MissingWrite");

        var diagnostics = validator.Validate(definition);

        diagnostics.Select(item => item.Code).Should().BeEquivalentTo(
        [
            ScreenDefinitionBindingValidator.ReadBindingMissing,
            ScreenDefinitionBindingValidator.ReadBindingUsesWrite,
            ScreenDefinitionBindingValidator.WriteBindingUsesRead,
            ScreenDefinitionBindingValidator.WriteBindingMissing,
        ]);
    }

    [Fact]
    public void Read_write_and_bridge_bindings_with_surrounding_whitespace_are_not_normalized()
    {
        var validator = Validator(
            queries:
            [
                Read("DEMO.Read", "demo:read"),
                Write("DEMO.Write", "demo:manage"),
            ],
            bridgePermissions: new Dictionary<string, string?>
            {
                ["bridge:demo.execute"] = "demo:execute",
            });
        var definition = new ScreenDefinition(
            "NON_CANONICAL_IDS",
            "Non-canonical binding IDs",
            Array.Empty<FieldDefinition>(),
            Layout: new RowNode
            {
                Children =
                [
                    new GridWidget { QueryId = " DEMO.Read" },
                    new FormWidget { SaveQueryId = "DEMO.Write " },
                    new ButtonWidget
                    {
                        Label = "Bridge",
                        Command = " bridge:demo.execute ",
                        RequiredPermission = "demo:execute",
                    },
                ],
            });

        var diagnostics = validator.Validate(definition);

        diagnostics.Should().HaveCount(3);
        diagnostics.Should().OnlyContain(item =>
            item.Code == ScreenDefinitionBindingValidator.BindingIdNotCanonical);
        diagnostics.Should().ContainSingle(item =>
            item.BindingPath == "layout.children[0].queryId" && item.BindingId == " DEMO.Read");
        diagnostics.Should().ContainSingle(item =>
            item.BindingPath == "layout.children[1].saveQueryId" && item.BindingId == "DEMO.Write ");
        diagnostics.Should().ContainSingle(item =>
            item.BindingPath == "layout.children[2].command" && item.BindingId == " bridge:demo.execute ");
    }

    [Fact]
    public void Layout_mode_audits_layout_and_search_reads_but_ignores_flat_reads()
    {
        var validator = Validator(queries: Array.Empty<QueryDefinition>());
        var definition = new ScreenDefinition(
            "ALL_READ_SURFACES",
            "All read surfaces",
            [new FieldDefinition("field", "Field", OptionsQueryId: "Missing.FieldOptions")],
            QueryId: "Missing.Query",
            CountQueryId: "Missing.Count",
            SearchFields:
            [
                new FieldDefinition("search", "Search", OptionsQueryId: "Missing.SearchOptions"),
            ],
            Layout: new SectionNode
            {
                Children =
                [
                    new GridWidget { QueryId = "Missing.Grid" },
                    new KpiWidget { QueryId = "Missing.Kpi" },
                    new BadgeWidget { QueryId = "Missing.Badge" },
                    new TrendChartWidget { QueryId = "Missing.Trend" },
                    new FormWidget
                    {
                        Fields =
                        [
                            new FieldWidget
                            {
                                Field = new FieldDefinition(
                                    "nested", "Nested", OptionsQueryId: "Missing.NestedOptions"),
                            },
                        ],
                    },
                ],
            });

        var diagnostics = validator.Validate(definition);

        diagnostics.Should().HaveCount(6);
        diagnostics.Should().OnlyContain(item =>
            item.Code == ScreenDefinitionBindingValidator.ReadBindingMissing);
        diagnostics.Select(item => item.BindingId).Should().BeEquivalentTo(
        [
            "Missing.SearchOptions",
            "Missing.Grid",
            "Missing.Kpi",
            "Missing.Badge",
            "Missing.Trend",
            "Missing.NestedOptions",
        ]);
    }

    [Fact]
    public void Collection_fields_are_recursively_audited_for_option_binding_and_permission()
    {
        var validator = Validator(queries: [Read("QMS.SpecCombo", "qms:read")]);
        var definition = new ScreenDefinition(
            "COLLECTION_BINDINGS",
            "Collection bindings",
            Array.Empty<FieldDefinition>(),
            Layout: new CollectionWidget
            {
                CollectionKey = "items",
                Fields =
                [
                    new FieldWidget
                    {
                        RequiredPermission = "other:read",
                        Field = new FieldDefinition(
                            "specId", "Spec", FieldType.Select, OptionsQueryId: "QMS.SpecCombo"),
                    },
                    new FieldWidget
                    {
                        Field = new FieldDefinition(
                            "equipmentId", "Equipment", FieldType.Select, OptionsQueryId: "Missing.EquipmentCombo"),
                    },
                ],
            });

        var diagnostics = validator.Validate(definition);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionBindingValidator.BindingPermissionMismatch
            && item.BindingPath == "layout.fields[0].field.optionsQueryId"
            && item.BindingId == "QMS.SpecCombo");
        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionBindingValidator.ReadBindingMissing
            && item.BindingPath == "layout.fields[1].field.optionsQueryId"
            && item.BindingId == "Missing.EquipmentCombo");
    }

    [Theory]
    [InlineData("", 0, null, ScreenDefinitionBindingValidator.CollectionKeyMissing, "layout.collectionKey")]
    [InlineData("items", -1, null, ScreenDefinitionBindingValidator.CollectionMinimumInvalid, "layout.minItems")]
    [InlineData("items", 2, 1, ScreenDefinitionBindingValidator.CollectionMaximumInvalid, "layout.maxItems")]
    [InlineData("items", 0, -1, ScreenDefinitionBindingValidator.CollectionMaximumInvalid, "layout.maxItems")]
    public void Invalid_collection_structure_is_rejected_before_runtime(
        string collectionKey,
        int minItems,
        int? maxItems,
        string expectedCode,
        string expectedPath)
    {
        var definition = new ScreenDefinition(
            "INVALID_COLLECTION",
            "Invalid collection",
            Array.Empty<FieldDefinition>(),
            Layout: new CollectionWidget
            {
                CollectionKey = collectionKey,
                MinItems = minItems,
                MaxItems = maxItems,
            });

        Validator(Array.Empty<QueryDefinition>()).Validate(definition)
            .Should().ContainSingle(item =>
                item.Code == expectedCode
                && item.BindingPath == expectedPath
                && item.Severity == ScreenCapabilityDiagnosticSeverity.Error);
    }

    [Fact]
    public void Flat_mode_audits_field_options_query_and_count_for_rendered_surfaces()
    {
        var validator = Validator(queries: Array.Empty<QueryDefinition>());
        var definition = new ScreenDefinition(
            "ACTIVE_FLAT_READS",
            "Active flat reads",
            [new FieldDefinition("field", "Field", OptionsQueryId: "Missing.FieldOptions")],
            Columns: [new GridColumnDefinition("ID", "ID")],
            QueryId: "Missing.Query",
            CountQueryId: "Missing.Count");

        var diagnostics = validator.Validate(definition);

        diagnostics.Should().HaveCount(3);
        diagnostics.Should().OnlyContain(item =>
            item.Code == ScreenDefinitionBindingValidator.ReadBindingMissing);
        diagnostics.Select(item => item.BindingId).Should().BeEquivalentTo(
        [
            "Missing.FieldOptions",
            "Missing.Query",
            "Missing.Count",
        ]);
    }

    [Fact]
    public void Flat_mode_ignores_inactive_form_and_grid_bindings_but_keeps_search_options()
    {
        var validator = Validator(queries: Array.Empty<QueryDefinition>());
        var definition = new ScreenDefinition(
            "INACTIVE_FLAT_SURFACES",
            "Inactive flat surfaces",
            Array.Empty<FieldDefinition>(),
            Columns: Array.Empty<GridColumnDefinition>(),
            QueryId: "Missing.HiddenRead",
            SaveQueryId: "bridge:missing.hidden-save",
            CountQueryId: "Missing.HiddenCount",
            DeleteQueryId: "Missing.HiddenDelete",
            BulkCommands: [new BulkCommandDefinition("Hidden bulk", "bridge:missing.hidden-bulk")],
            SearchFields:
            [
                new FieldDefinition("search", "Search", OptionsQueryId: "Missing.ActiveSearchOptions"),
            ]);

        var diagnostics = validator.Validate(definition);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionBindingValidator.ReadBindingMissing
            && item.BindingPath == "searchFields[0].optionsQueryId"
            && item.BindingId == "Missing.ActiveSearchOptions");
    }

    [Fact]
    public void Flat_grid_actions_require_both_columns_and_query()
    {
        var validator = Validator(queries: Array.Empty<QueryDefinition>());
        var definition = new ScreenDefinition(
            "FLAT_GRID_WITHOUT_QUERY",
            "Flat grid without query",
            Array.Empty<FieldDefinition>(),
            Columns: [new GridColumnDefinition("ID", "ID")],
            CountQueryId: "Missing.HiddenCount",
            DeleteQueryId: "bridge:missing.hidden-delete",
            BulkCommands: [new BulkCommandDefinition("Hidden bulk", "Missing.HiddenBulk")]);

        validator.Validate(definition).Should().BeEmpty();
    }

    [Fact]
    public void Every_named_write_and_bridge_surface_is_audited()
    {
        var validator = Validator(queries: [Read("DEMO.Grid", "demo:read")]);
        var definition = new ScreenDefinition(
            "ALL_WRITE_SURFACES",
            "All write surfaces",
            Array.Empty<FieldDefinition>(),
            SaveQueryId: "Missing.Save",
            DeleteQueryId: "Missing.Delete",
            BulkCommands: [new BulkCommandDefinition("Bulk", "Missing.Bulk")],
            Layout: new RowNode
            {
                Children =
                [
                    new GridWidget { QueryId = "DEMO.Grid" },
                    new FormWidget { SaveQueryId = "Missing.Form" },
                    new ButtonWidget
                    {
                        Label = "Named button",
                        Command = "Missing.Button",
                        RequiredPermission = "demo:manage",
                    },
                    new ButtonWidget
                    {
                        Label = "Bridge button",
                        Command = "bridge:missing.button",
                        RequiredPermission = "demo:manage",
                    },
                ],
            });

        var diagnostics = validator.Validate(definition);

        diagnostics.Should().HaveCount(5);
        diagnostics.Count(item =>
            item.Code == ScreenDefinitionBindingValidator.WriteBindingMissing).Should().Be(4);
        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionBindingValidator.BridgeCommandMissing);
        diagnostics.Should().NotContain(item => item.BindingId == "Missing.Save");
    }

    [Fact]
    public void Layout_without_grid_ignores_top_level_delete_and_bulk_commands()
    {
        var validator = Validator(queries: Array.Empty<QueryDefinition>());
        var definition = new ScreenDefinition(
            "LAYOUT_WITHOUT_GRID",
            "Layout without grid",
            [new FieldDefinition("hidden", "Hidden", OptionsQueryId: "Missing.HiddenFieldOptions")],
            QueryId: "Missing.HiddenQuery",
            SaveQueryId: "bridge:missing.hidden-save",
            CountQueryId: "Missing.HiddenCount",
            DeleteQueryId: "bridge:missing.hidden-delete",
            BulkCommands: [new BulkCommandDefinition("Hidden bulk", "Missing.HiddenBulk")],
            Layout: new ButtonWidget
            {
                Label = "Active bridge",
                Command = "bridge:missing.active",
                RequiredPermission = "demo:manage",
            });

        var diagnostics = validator.Validate(definition);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionBindingValidator.BridgeCommandMissing
            && item.BindingPath == "layout.command"
            && item.BindingId == "bridge:missing.active");
    }

    [Fact]
    public void Layout_grid_without_query_does_not_activate_top_level_delete_or_bulk_commands()
    {
        var validator = Validator(queries: Array.Empty<QueryDefinition>());
        var definition = new ScreenDefinition(
            "LAYOUT_EMPTY_GRID",
            "Layout empty grid",
            Array.Empty<FieldDefinition>(),
            DeleteQueryId: "bridge:missing.hidden-delete",
            BulkCommands: [new BulkCommandDefinition("Hidden bulk", "Missing.HiddenBulk")],
            Layout: new GridWidget());

        validator.Validate(definition).Should().BeEmpty();
    }

    [Fact]
    public void Named_and_bridge_permissions_are_checked_at_binding_nodes()
    {
        var validator = Validator(
            queries:
            [
                Read("DEMO.Read", "demo:read"),
                Write("DEMO.Write", "demo:manage"),
            ],
            bridgePermissions: new Dictionary<string, string?>
            {
                ["bridge:demo.execute"] = "demo:execute",
            });
        var definition = new ScreenDefinition(
            "BROKEN_PERMISSIONS",
            "Broken permissions",
            Array.Empty<FieldDefinition>(),
            Layout: new SectionNode
            {
                Children =
                [
                    new GridWidget { QueryId = "DEMO.Read", RequiredPermission = "other:read" },
                    new ButtonWidget { Label = "Named", Command = "DEMO.Write" },
                    new ButtonWidget
                    {
                        Label = "Bridge",
                        Command = "bridge:demo.execute",
                        RequiredPermission = "other:execute",
                    },
                ],
            });

        var diagnostics = validator.Validate(definition);

        diagnostics.Should().ContainSingle(item =>
            item.Code == ScreenDefinitionBindingValidator.ButtonPermissionMissing
            && item.BindingId == "DEMO.Write");
        diagnostics.Count(item =>
            item.Code == ScreenDefinitionBindingValidator.BindingPermissionMismatch).Should().Be(2);
    }

    [Fact]
    public void Flat_and_bulk_permission_metadata_is_compared_with_catalog()
    {
        var validator = Validator(
            queries:
            [
                Read("DEMO.Read", "demo:read"),
                Write("DEMO.Save", "demo:manage"),
                Write("DEMO.Delete", "demo:manage"),
                Write("DEMO.Bulk", "demo:execute"),
            ]);
        var definition = new ScreenDefinition(
            "FLAT_PERMISSION_MISMATCH",
            "Flat permission mismatch",
            [new FieldDefinition("value", "Value")],
            Columns: [new GridColumnDefinition("ID", "ID")],
            QueryId: "DEMO.Read",
            SaveQueryId: "DEMO.Save",
            DeleteQueryId: "DEMO.Delete",
            BulkCommands:
            [
                new BulkCommandDefinition(
                    "Bulk",
                    "DEMO.Bulk",
                    RequiredPermission: "other:execute"),
            ],
            ReadRequiredPermission: "other:read",
            SaveRequiredPermission: "other:manage",
            DeleteRequiredPermission: "other:manage");

        var diagnostics = validator.Validate(definition);

        diagnostics.Should().HaveCount(4);
        diagnostics.Should().OnlyContain(item =>
            item.Code == ScreenDefinitionBindingValidator.BindingPermissionMismatch);
        diagnostics.Select(item => item.BindingPath).Should().BeEquivalentTo(
        [
            "queryId",
            "saveQueryId",
            "deleteQueryId",
            "bulkCommands[0].commandQueryId",
        ]);
    }

    [Fact]
    public void Missing_bridge_and_missing_catalog_permissions_are_reported()
    {
        var validator = Validator(
            queries:
            [
                new QueryDefinition("DEMO.UnprotectedRead", "SELECT 1", "test"),
                new QueryDefinition("DEMO.UnprotectedWrite", "UPDATE X SET Y=1", "test", IsWrite: true),
            ],
            bridgePermissions: new Dictionary<string, string?>
            {
                ["bridge:demo.unprotected"] = null,
            });
        var definition = new ScreenDefinition(
            "BROKEN_CATALOG_PERMISSIONS",
            "Broken catalog permissions",
            [new FieldDefinition("value", "Value")],
            Columns: [new GridColumnDefinition("ID", "ID")],
            QueryId: "DEMO.UnprotectedRead",
            SaveQueryId: "DEMO.UnprotectedWrite",
            BulkCommands:
            [
                new BulkCommandDefinition("Missing bridge", "bridge:demo.missing"),
                new BulkCommandDefinition("Unprotected bridge", "bridge:demo.unprotected"),
            ]);

        var diagnostics = validator.Validate(definition);

        diagnostics.Select(item => item.Code).Should().BeEquivalentTo(
        [
            ScreenDefinitionBindingValidator.ReadBindingPermissionMissing,
            ScreenDefinitionBindingValidator.WriteBindingPermissionMissing,
            ScreenDefinitionBindingValidator.BridgeCommandMissing,
            ScreenDefinitionBindingValidator.WriteBindingPermissionMissing,
        ]);
    }

    [Fact]
    public void Host_required_aggregate_command_is_valid_only_on_bulk_surface()
    {
        const string aggregateCommand = "bridge:demo.aggregate";
        var validator = Validator(
            queries: [Read("DEMO.Read", "demo:read")],
            bridgeDescriptors:
            [
                new MetaCommandDescriptor(
                    aggregateCommand,
                    RequiredPermission: "demo:manage",
                    ExecutionMode: MetaCommandExecutionMode.HostRequiredAggregate,
                    Effect: MetaCommandEffect.Mutating),
            ]);
        var definition = new ScreenDefinition(
            "AGGREGATE_SURFACES",
            "Aggregate surfaces",
            Array.Empty<FieldDefinition>(),
            DeleteQueryId: aggregateCommand,
            BulkCommands: [new BulkCommandDefinition("Aggregate", aggregateCommand)],
            Layout: new SectionNode
            {
                Children =
                [
                    new GridWidget { QueryId = "DEMO.Read" },
                    new FormWidget { SaveQueryId = aggregateCommand },
                    new ButtonWidget
                    {
                        Label = "Aggregate",
                        Command = aggregateCommand,
                        RequiredPermission = "demo:manage",
                    },
                ],
            });

        var diagnostics = validator.Validate(definition);

        diagnostics.Should().HaveCount(3);
        diagnostics.Should().OnlyContain(item =>
            item.Code == ScreenDefinitionBindingValidator.BridgeCommandExecutionModeMismatch);
        diagnostics.Select(item => item.BindingPath).Should().BeEquivalentTo(
            "deleteQueryId",
            "layout.children[1].saveQueryId",
            "layout.children[2].command");
        diagnostics.Should().NotContain(item => item.BindingPath.StartsWith("bulkCommands", StringComparison.Ordinal));
    }

    [Fact]
    public void Non_mutating_command_is_allowed_for_button_and_bulk_but_not_save_or_delete()
    {
        const string exportCommand = "bridge:demo.export";
        var validator = Validator(
            queries: [Read("DEMO.Read", "demo:read")],
            bridgeDescriptors:
            [
                new MetaCommandDescriptor(
                    exportCommand,
                    RequiredPermission: "demo:read",
                    Effect: MetaCommandEffect.NonMutating),
            ]);
        var definition = new ScreenDefinition(
            "EXPORT_SURFACES",
            "Export surfaces",
            Array.Empty<FieldDefinition>(),
            DeleteQueryId: exportCommand,
            BulkCommands: [new BulkCommandDefinition("Export selected", exportCommand)],
            Layout: new SectionNode
            {
                Children =
                [
                    new GridWidget { QueryId = "DEMO.Read" },
                    new FormWidget { SaveQueryId = exportCommand },
                    new ButtonWidget
                    {
                        Label = "Export",
                        Command = exportCommand,
                        RequiredPermission = "demo:read",
                    },
                ],
            });

        var diagnostics = validator.Validate(definition);

        diagnostics.Should().HaveCount(2);
        diagnostics.Should().OnlyContain(item =>
            item.Code == ScreenDefinitionBindingValidator.BridgeCommandEffectMismatch);
        diagnostics.Select(item => item.BindingPath).Should().BeEquivalentTo(
            "deleteQueryId",
            "layout.children[1].saveQueryId");
    }

    [Fact]
    public async Task Every_canonical_code_seed_has_zero_contextual_binding_errors()
    {
        _ = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ICodeScreenDefinitionCatalog>();
        var validator = scope.ServiceProvider.GetRequiredService<IScreenDefinitionBindingValidator>();
        var definitions = await catalog.ListAsync();

        var diagnostics = definitions
            .SelectMany(validator.Validate)
            .OrderBy(item => item.UiId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.BindingPath, StringComparer.Ordinal)
            .ToArray();

        _output.WriteLine($"Canonical seed definitions: {definitions.Count}");
        _output.WriteLine($"Contextual binding errors: {diagnostics.Length}");
        _output.WriteLine(string.Join(
            Environment.NewLine,
            diagnostics.Select(item =>
                $"{item.UiId} [{item.Code}] {item.BindingPath}={item.BindingId}: {item.Message}")));

        definitions.Count.Should().BeGreaterThanOrEqualTo(270);
        diagnostics.Should().BeEmpty(string.Join(
            Environment.NewLine,
            diagnostics.Select(item =>
                $"{item.UiId} [{item.Code}] {item.BindingPath}={item.BindingId}: {item.Message}")));
    }

    private static ScreenDefinitionBindingValidator Validator(
        IEnumerable<QueryDefinition> queries,
        IReadOnlyDictionary<string, string?>? bridgePermissions = null,
        IReadOnlyCollection<MetaCommandDescriptor>? bridgeDescriptors = null)
        => new(
            new StubQueryRegistry(queries),
            new StubCommandCatalog(
                bridgeDescriptors
                ?? (bridgePermissions ?? new Dictionary<string, string?>())
                    .Select(pair => new MetaCommandDescriptor(pair.Key, pair.Value))
                    .ToArray()));

    private static QueryDefinition Read(string id, string permission)
        => new(id, "SELECT 1", "test", permission);

    private static QueryDefinition Write(string id, string permission)
        => new(id, "UPDATE X SET Y=1", "test", permission, IsWrite: true);

    private sealed class StubQueryRegistry : IQueryRegistry
    {
        private readonly IReadOnlyDictionary<string, QueryDefinition> _queries;

        public StubQueryRegistry(IEnumerable<QueryDefinition> queries)
            => _queries = queries.ToDictionary(item => item.Id, StringComparer.Ordinal);

        public bool TryGet(string queryId, out QueryDefinition? definition)
            => _queries.TryGetValue(queryId, out definition);

        public IReadOnlyCollection<string> Ids => _queries.Keys.ToArray();
        public string Dialect => "test";
    }

    private sealed class StubCommandCatalog : IMetaCommandDriverCatalog
    {
        private readonly IReadOnlyDictionary<string, MetaCommandDescriptor> _commands;

        public StubCommandCatalog(IEnumerable<MetaCommandDescriptor> commands)
            => _commands = commands.ToDictionary(
                command => command.Id,
                command => command,
                StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<MetaCommandDescriptor> Commands => _commands.Values.ToArray();

        public bool Contains(string commandId) => _commands.ContainsKey(commandId);

        public MetaCommandAvailability CanExecute(
            string commandId,
            IReadOnlyDictionary<string, object?> parameters,
            MetaCommandExecutionContext context)
            => throw new NotSupportedException();

        public Task<MetaCommandResult> ExecuteAsync(
            string commandId,
            IReadOnlyDictionary<string, object?> parameters,
            MetaCommandExecutionContext context,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}

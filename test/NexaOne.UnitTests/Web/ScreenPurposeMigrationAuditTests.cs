using NexaOne.Web.Services.Meta;
using Xunit.Abstractions;

namespace NexaOne.UnitTests.Web;

public sealed class ScreenPurposeMigrationAuditTests(ITestOutputHelper output)
{
    [Fact]
    public void Audit_uses_active_surfaces_command_descriptors_and_reviewed_business_gaps()
    {
        const string exportCommand = "bridge:demo.export";
        var definitions = new ScreenDefinition[]
        {
            new(
                "READ_ONLY_EXPORT",
                "내보내기 조회",
                Array.Empty<FieldDefinition>(),
                Columns: [new GridColumnDefinition("ID", "ID")],
                QueryId: "DEMO.List",
                BulkCommands: [new BulkCommandDefinition("내보내기", exportCommand)]),
            new(
                "HIDDEN_FLAT_SAVE",
                "레이아웃에서 숨겨진 평면 저장",
                [new FieldDefinition("name", "이름")],
                SaveQueryId: "DEMO.Save",
                Layout: new TextWidget { Text = "표시 전용" }),
            new(
                "REVIEWED_REGISTER_GAP",
                "결과 등록",
                Array.Empty<FieldDefinition>(),
                Columns: [new GridColumnDefinition("ID", "ID")],
                QueryId: "DEMO.List"),
        };
        var catalog = new MetaCommandDriverCatalog(
        [
            new DescriptorOnlyMetaCommandDriver(new MetaCommandDescriptor(
                exportCommand,
                Effect: MetaCommandEffect.NonMutating)),
        ]);

        var report = ScreenPurposeMigrationAudit.InspectAuto(
            definitions,
            catalog,
            ["REVIEWED_REGISTER_GAP"]);

        report.Single(item => item.UiId == "READ_ONLY_EXPORT").Group
            .Should().Be(ScreenPurposeMigrationGroup.StructurallyReadyReadOnly);
        report.Single(item => item.UiId == "HIDDEN_FLAT_SAVE").Group
            .Should().Be(ScreenPurposeMigrationGroup.ImplementationGap);
        report.Single(item => item.UiId == "REVIEWED_REGISTER_GAP").Group
            .Should().Be(ScreenPurposeMigrationGroup.ImplementationGap);
    }

    [Fact]
    public async Task Seeded_auto_audit_is_deterministic_and_only_contains_documented_retentions()
    {
        var provider = new InMemoryScreenDefinitionProvider();
        var ids = await provider.GetKnownUiIdsAsync();
        var definitions = ids
            .Select(provider.Get)
            .Where(definition => definition is not null)
            .Cast<ScreenDefinition>();
        var catalog = new MetaCommandDriverCatalog([new MrpConversionMetaCommandDriver()]);

        var report = ScreenPurposeMigrationAudit.InspectAuto(definitions, catalog);
        var formatted = ScreenPurposeMigrationAudit.Format(report);
        output.WriteLine(formatted);

        report.Select(item => item.UiId).Should().Equal(
            SeedScreenPurposeDecisions.RetainedAutoReasons.Keys
                .Order(StringComparer.OrdinalIgnoreCase));
        report.Should().OnlyContain(item => item.Group == ScreenPurposeMigrationGroup.ImplementationGap);
        report.Select(item => item.UiId).Should().OnlyContain(uiId =>
            SeedScreenPurposeDecisions.RetainedAutoReasons.ContainsKey(uiId));
        SeedScreenPurposeDecisions.RetainedAutoReasons.Values
            .Should().OnlyContain(reason => !string.IsNullOrWhiteSpace(reason));
        ScreenPurposeMigrationAudit.Format(report.Reverse()).Should().Be(formatted);
    }

    [Fact]
    public async Task Seeded_explicit_decisions_cover_the_reviewed_auto_catalog_and_match_capabilities()
    {
        var provider = new InMemoryScreenDefinitionProvider();
        var definitions = (await provider.GetKnownUiIdsAsync())
            .Select(provider.Get)
            .Where(definition => definition is not null)
            .Cast<ScreenDefinition>()
            .DistinctBy(definition => definition.UiId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        SeedScreenPurposeDecisions.ExplicitDecisions.Should().HaveCount(158);
        SeedScreenPurposeDecisions.ExplicitDecisions.Values
            .Count(purpose => purpose == ScreenPurpose.Inquiry).Should().Be(150);
        SeedScreenPurposeDecisions.ExplicitDecisions.Values
            .Count(purpose => purpose == ScreenPurpose.Manage).Should().Be(8);

        var decided = definitions
            .Where(definition => SeedScreenPurposeDecisions.ExplicitDecisions.ContainsKey(definition.UiId))
            .ToArray();
        decided.Should().HaveCount(SeedScreenPurposeDecisions.ExplicitDecisions.Count);
        decided.Should().OnlyContain(definition =>
            definition.Purpose == SeedScreenPurposeDecisions.ExplicitDecisions[definition.UiId]);
        decided.SelectMany(ScreenDefinitionCapabilityValidator.Validate)
            .Should().NotContain(diagnostic =>
                diagnostic.Severity == ScreenCapabilityDiagnosticSeverity.Error);
    }

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

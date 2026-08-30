using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace NexaOne.UnitTests.Architecture;

/// <summary>
/// 업무 Module이 제품 호스트나 다른 업무 Module의 implementation에 결합하지 않도록 project 의존을 검증합니다.
/// 재사용 Framework에도 같은 방향을 적용해 제품 참조가 Framework로 역류하는 것을 차단합니다.
/// </summary>
public sealed class ModuleDependencyBoundaryTests
{
    private static readonly string RepoRoot = RepositorySource.Root;
    private static readonly string ModulesRoot = Path.Combine(RepoRoot, "src", "04.Modules");
    private static readonly string ProjectsRoot = Path.Combine(RepoRoot, "src", "05.Projects");
    private static readonly string ServerRoot = Path.Combine(RepoRoot, "src", "00.Main", "NexaOne.Server");
    private static readonly string CommonProject = Path.Combine(
        RepoRoot, "src", "02.Backend", "NexaOne.Common", "NexaOne.Common.csproj");

    private static readonly string[] ReusableFrameworkProjects =
    [
        Path.Combine(RepoRoot, "submodules", "NexaFramework", "src", "NexaFramework", "NexaFramework.csproj"),
        Path.Combine(RepoRoot, "submodules", "NexaFramework", "src", "NexaFramework.Hosting", "NexaFramework.Hosting.csproj"),
    ];

    private static readonly string[] ProductAssemblyRoots =
    [
        "NexaOne",
        "NexusOne",
        "NexaMes",
        "MES",
    ];

    private static readonly ApprovedProjection[] ApprovedForeignSchemaProjections =
    [
        new(
            Path.Combine("src", "04.Modules", "NexaOne.POM", "Infrastructure", "LegacySalesOrderMrpProjection.cs"),
            "SLS_SALES_ORDER",
            Path.Combine("docs", "adr", "0002-temporary-sls-mrp-demand-projection.md")),
        new(
            Path.Combine("src", "04.Modules", "NexaOne.SYS", "Infrastructure", "MaintenanceIdentityDirectory.cs"),
            "MDM_WORKER_USER_MAP",
            Path.Combine("docs", "adr", "0003-maintenance-identity-projection-ownership.md")),
    ];

    private static readonly IReadOnlyDictionary<string, string> PhysicalSchemaOwners =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MDM"] = "MDM",
            ["EST"] = "EST",
            ["FDC"] = "FDC",
            ["RMS"] = "RMS",
            ["QMS"] = "QMS",
            ["EMS"] = "EMS",
            ["IVT"] = "IVT",
            ["POM"] = "POM",
            ["MRP"] = "POM",
            ["SHP"] = "SHP",
            ["SYS"] = "SYS",
            ["SLS"] = "SLS",
            ["PRC"] = "PRC",
        };

    [Fact]
    public void Every_domain_module_directory_owns_exactly_one_project()
    {
        var moduleDirectories = FindModuleDirectories();

        moduleDirectories.Should().NotBeEmpty("최소 하나의 업무 Module이 검색되어야 공허 통과하지 않습니다");
        foreach (var directory in moduleDirectories)
        {
            Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
                .Should().ContainSingle($"'{Path.GetFileName(directory)}' Module은 단일 project 진입점을 가져야 합니다");
        }
    }

    [Fact]
    public void Domain_modules_do_not_reference_server_or_other_domain_modules()
    {
        var moduleProjects = FindModuleProjects();
        var moduleNames = moduleProjects
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();

        foreach (var project in moduleProjects)
        {
            var sourceName = Path.GetFileNameWithoutExtension(project);
            foreach (var reference in ReadDeclaredReferences(project))
            {
                if (reference.Kind == "ProjectReference")
                {
                    var targetPath = ResolveProjectReference(project, reference.Include);
                    if (IsWithin(targetPath, ServerRoot))
                        violations.Add($"{sourceName} -> NexaOne.Server ({reference.Include})");
                    else if (IsWithin(targetPath, ModulesRoot)
                             && !PathsEqual(targetPath, project))
                        violations.Add($"{sourceName} -> {Path.GetFileNameWithoutExtension(targetPath)} ({reference.Include})");
                    continue;
                }

                var targetName = DependencyName(reference.Include);
                if (targetName.Equals("NexaOne.Server", StringComparison.OrdinalIgnoreCase)
                    || (moduleNames.Contains(targetName)
                        && !targetName.Equals(sourceName, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{sourceName} -> {targetName} ({reference.Kind})");
                }
            }
        }

        violations.Should().BeEmpty(
            "업무 Module은 호스트나 다른 업무 Module implementation을 직접 참조하지 않고 공유 계약/이벤트 Seam을 사용해야 합니다");
    }

    [Fact]
    public void Project_plugins_reference_only_the_shared_common_contract_project()
    {
        Directory.Exists(ProjectsRoot).Should().BeTrue(
            "project-specific policies must have an explicit architecture boundary");
        var projects = Directory.GetFiles(ProjectsRoot, "*.csproj", SearchOption.AllDirectories);
        projects.Should().NotBeEmpty(
            "the project-plugin boundary test must not pass without inspecting a project plugin");

        var violations = new List<string>();
        foreach (var project in projects)
        {
            var references = ReadDeclaredReferences(project);
            var commonReferences = references
                .Where(reference => reference.Kind == "ProjectReference"
                                    && PathsEqual(
                                        ResolveProjectReference(project, reference.Include),
                                        CommonProject))
                .ToArray();
            if (commonReferences.Length != 1)
            {
                violations.Add(
                    $"{Path.GetFileNameWithoutExtension(project)} -> expected exactly one shared contract reference");
            }

            violations.AddRange(references
                .Where(reference => reference.Kind != "ProjectReference"
                                    || !PathsEqual(
                                        ResolveProjectReference(project, reference.Include),
                                        CommonProject))
                .Select(reference =>
                    $"{Path.GetFileNameWithoutExtension(project)} -> {reference.Include} ({reference.Kind})"));

            var projectDirectory = Path.GetDirectoryName(project)!;
            foreach (var sourceFile in Directory.GetFiles(
                         projectDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(sourceFile);
                var imports = Regex.Matches(
                    source,
                    @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*)?(?<namespace>NexaOne(?:\.[A-Za-z_]\w*)+)",
                    RegexOptions.Multiline | RegexOptions.CultureInvariant);
                violations.AddRange(imports
                    .Select(static match => match.Groups["namespace"].Value)
                    .Where(static importedNamespace => !importedNamespace.StartsWith(
                        "NexaOne.ServiceContracts.", StringComparison.Ordinal))
                    .Select(importedNamespace =>
                        $"{Path.GetRelativePath(RepoRoot, sourceFile)} -> {importedNamespace} (source import)"));

                var qualifiedNames = Regex.Matches(
                    source,
                    @"\bNexaOne(?:\.[A-Za-z_]\w*)+",
                    RegexOptions.CultureInvariant);
                violations.AddRange(qualifiedNames
                    .Select(static match => match.Value)
                    .Where(static qualifiedName =>
                        !qualifiedName.StartsWith("NexaOne.ServiceContracts.", StringComparison.Ordinal)
                        && !qualifiedName.StartsWith("NexaOne.Project.", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .Select(qualifiedName =>
                        $"{Path.GetRelativePath(RepoRoot, sourceFile)} -> {qualifiedName} (qualified source reference)"));
            }
        }

        violations.Should().BeEmpty(
            "project plugins may use only the shared ServiceContracts seam and must remain free of Server, module, database, I/O, or package dependencies");
    }

    [Fact]
    public void Application_sources_do_not_import_their_module_infrastructure_or_plc_contracts()
    {
        var violations = new List<string>();
        foreach (var moduleDirectory in FindModuleDirectories())
        {
            var moduleName = Path.GetFileName(moduleDirectory);
            var applicationDirectory = Path.Combine(moduleDirectory, "Application");
            if (!Directory.Exists(applicationDirectory)) continue;

            foreach (var file in Directory.GetFiles(applicationDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(file);
                var ownInfrastructure = $"using {moduleName}.Infrastructure";
                if (source.Contains(ownInfrastructure, StringComparison.Ordinal)
                    || source.Contains("using NexaLogic.Plc.", StringComparison.Ordinal))
                {
                    violations.Add(Path.GetRelativePath(RepoRoot, file));
                }
            }
        }

        violations.Should().BeEmpty(
            "Application은 자체 Infrastructure나 PLC 공급자 계약 대신 모듈 소유 입력/출력 포트를 사용해야 합니다");
    }

    [Fact]
    public void Host_production_quality_gateway_remains_a_thin_module_proxy()
    {
        var gatewayFiles = Directory.GetFiles(
            ServerRoot,
            "ProductionQualityGateway*.cs",
            SearchOption.AllDirectories);
        gatewayFiles.Should().ContainSingle(
            "생산 품질 정책과 SQL은 QMS 모듈이 소유하고 호스트에는 단일 프록시만 있어야 합니다");

        var source = File.ReadAllText(gatewayFiles.Single());
        source.Should().Contain("ModuleBeanResolver");
        source.Should().Contain("qmsProductionQualityGateway");
        source.Should().NotContain("ApplicationServer.GetInstance()");
        source.Should().NotContain("QueryRepository");
        source.Should().NotContain("EesDataSource");
        source.Should().NotContain("QMS_");
        source.Should().NotContain("SELECT ");
        source.Should().NotContain("FROM ");
    }

    [Fact]
    public void Host_module_proxies_use_the_injected_resolver_not_the_global_server_locator()
    {
        var proxyFiles = Directory.GetFiles(
            Path.Combine(ServerRoot, "Gateway"), "*Proxy.cs", SearchOption.TopDirectoryOnly);
        proxyFiles.Should().NotBeEmpty();

        var globalLocatorUsers = proxyFiles
            .Where(file => File.ReadAllText(file).Contains(
                "ApplicationServer.GetInstance()", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .ToArray();

        globalLocatorUsers.Should().BeEmpty(
            "Spring sibling proxies must receive the host-owned resolver at the XML composition root");
    }

    [Fact]
    public void Spring_host_injects_one_module_resolver_into_every_sibling_proxy()
    {
        foreach (var configFile in new[] { "server.xml", "server.sqlite.xml" })
        {
            var document = XDocument.Load(Path.Combine(
                ServerRoot, "config", "host", configFile));
            var objects = document.Descendants()
                .Where(element => element.Name.LocalName == "object")
                .ToArray();

            var applicationServer = objects.Single(element =>
                (string?)element.Attribute("id") == "applicationServer");
            ((string?)applicationServer.Attribute("factory-method")).Should().Be("GetInstance");

            var resolver = objects.Single(element =>
                (string?)element.Attribute("id") == "moduleBeanResolver");
            resolver.Elements().Single(element => element.Name.LocalName == "constructor-arg")
                .Attribute("ref")?.Value.Should().Be("applicationServer");

            var proxies = objects.Where(element =>
                    ((string?)element.Attribute("type"))?.Contains(
                        "Proxy, NexaOne.Server", StringComparison.Ordinal) == true)
                .ToArray();
            proxies.Should().NotBeEmpty();
            foreach (var proxy in proxies)
            {
                proxy.Elements().Single(element => element.Name.LocalName == "constructor-arg")
                    .Attribute("ref")?.Value.Should().Be("moduleBeanResolver");
            }
        }
    }

    [Fact]
    public void Every_domain_module_has_one_code_composition_root_and_exports_only_factory_products()
    {
        var moduleDirectories = FindModuleDirectories();
        var moduleConfigRoot = Path.Combine(ServerRoot, "config", "modules");

        foreach (var directory in moduleDirectories)
        {
            var assemblyName = Path.GetFileName(directory);
            var moduleCode = Path.Combine(directory, "Module.cs");
            File.Exists(moduleCode).Should().BeTrue(
                $"{assemblyName}은 저장소와 application 구현 그래프를 숨기는 Module.cs 조립 진입점을 가져야 합니다");

            var moduleKey = assemblyName["NexaOne.".Length..].ToLowerInvariant();
            var configPath = Path.Combine(moduleConfigRoot, $"{moduleKey}.xml");
            File.Exists(configPath).Should().BeTrue(
                $"{assemblyName}의 Spring 경계는 module root와 공개 export만 선언해야 합니다");

            var objects = XDocument.Load(configPath)
                .Descendants()
                .Where(static element => element.Name.LocalName == "object")
                .ToArray();
            var rootId = $"{moduleKey}Module";
            var roots = objects.Where(element =>
                    string.Equals((string?)element.Attribute("id"), rootId, StringComparison.Ordinal))
                .ToArray();
            roots.Should().ContainSingle();

            var root = roots.Single();
            ((string?)root.Attribute("type")).Should().Be($"{assemblyName}.Module, {assemblyName}");
            root.Attribute("factory-object").Should().BeNull();
            root.Elements().Should().OnlyContain(static element => element.Name.LocalName == "constructor-arg",
                "Spring은 모듈 외부 의존성만 Module.cs에 전달해야 합니다");

            var exports = objects.Except(roots).ToArray();
            exports.Should().NotBeEmpty($"{assemblyName}은 최소 하나의 공개 bridge/worker를 export해야 합니다");
            foreach (var export in exports)
            {
                export.Attribute("type").Should().BeNull(
                    "모듈 내부 구현 타입은 Spring XML이 아니라 Module.cs가 소유해야 합니다");
                ((string?)export.Attribute("factory-object")).Should().Be(rootId);
                ((string?)export.Attribute("factory-method")).Should().NotBeNullOrWhiteSpace();
                export.Elements().Should().BeEmpty(
                    "factory export는 추가 의존성을 받아 모듈 캡슐화를 우회하지 않아야 합니다");
            }
        }
    }

    [Fact]
    public void Domain_sources_do_not_import_service_contract_dtos()
    {
        var violations = FindModuleDirectories()
            .Select(directory => Path.Combine(directory, "Domain"))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(file => File.ReadAllText(file).Contains(
                "using NexaOne.ServiceContracts.", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(RepoRoot, file))
            .ToArray();

        violations.Should().BeEmpty(
            "Domain은 모듈 간 전송 DTO 대신 자체 값 형식을 사용하고 Application 경계에서 변환해야 합니다");
    }

    [Fact]
    public void Domain_module_sources_do_not_reference_foreign_physical_tables_outside_approved_projections()
    {
        var physicalTables = DiscoverPhysicalTables();
        physicalTables.Should().NotBeEmpty("migration DDL에서 실제 업무 테이블을 찾아야 검사가 공허 통과하지 않습니다");

        var violations = new List<string>();
        var usedApprovals = new HashSet<ApprovedProjection>();
        foreach (var moduleDirectory in FindModuleDirectories())
        {
            var owner = Path.GetFileName(moduleDirectory)["NexaOne.".Length..].ToUpperInvariant();
            foreach (var file in Directory.GetFiles(moduleDirectory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(RepoRoot, file);
                var source = File.ReadAllText(file);
                foreach (var table in FindReferencedPhysicalTables(source, physicalTables))
                {
                    var tableOwner = GetPhysicalSchemaOwner(table);
                    if (tableOwner is null || string.Equals(tableOwner, owner, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var approval = ApprovedForeignSchemaProjections.SingleOrDefault(candidate =>
                        PathsEqual(Path.Combine(RepoRoot, candidate.SourcePath), file)
                        && string.Equals(candidate.Table, table, StringComparison.OrdinalIgnoreCase));
                    if (approval is not null)
                    {
                        File.Exists(Path.Combine(RepoRoot, approval.AdrPath)).Should().BeTrue(
                            $"승인 projection {relative} -> {table}의 ADR이 존재해야 합니다");
                        usedApprovals.Add(approval);
                        continue;
                    }

                    violations.Add($"{relative} -> {table}");
                }
            }
        }

        violations.Should().BeEmpty(
            "업무 Module은 다른 Module의 물리 테이블 대신 owner query/command contract를 사용해야 합니다");
        usedApprovals.Should().BeEquivalentTo(ApprovedForeignSchemaProjections,
            "allowlist는 실제로 사용되는 정확한 projection 예외만 포함해야 합니다");
    }

    [Theory]
    [InlineData("NexaOne.QMS", "Infrastructure/QmsReferenceRepository.cs")]
    [InlineData("NexaOne.QMS", "Infrastructure/InspectionResultRepository.cs")]
    [InlineData("NexaOne.POM", "Infrastructure/MrpPlanningRepository.cs")]
    public void Qms_and_pom_orchestrators_contain_no_foreign_schema_identifiers(
        string module,
        string relativeSource)
    {
        var owner = module["NexaOne.".Length..].ToUpperInvariant();
        var file = Path.Combine(ModulesRoot, module, relativeSource.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(file).Should().BeTrue();

        var foreignTables = FindReferencedPhysicalTables(File.ReadAllText(file), DiscoverPhysicalTables())
            .Where(table => GetPhysicalSchemaOwner(table) is { } tableOwner
                            && !string.Equals(tableOwner, owner, StringComparison.OrdinalIgnoreCase));
        foreignTables.Should().BeEmpty(
            "QMS/POM orchestration 저장소는 foreign schema SQL을 소유하지 않아야 합니다");
    }

    [Fact]
    public void Ivt_trace_projection_does_not_reference_fdc_physical_tables()
    {
        var ivtRoot = Path.Combine(ModulesRoot, "NexaOne.IVT");
        var sources = Directory.GetFiles(ivtRoot, "*.cs", SearchOption.AllDirectories)
            .Append(Path.Combine(
                ServerRoot,
                "config",
                "db",
                "migrations",
                "V114__IVT_TRACE_PROJECTION.sql"));
        var violations = sources
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return source.Contains("FDC_COLLECT_DATA", StringComparison.OrdinalIgnoreCase)
                       || source.Contains("FDC_PARAMETER", StringComparison.OrdinalIgnoreCase);
            })
            .Select(file => Path.GetRelativePath(RepoRoot, file))
            .ToArray();

        violations.Should().BeEmpty(
            "IVT는 FDC 물리 테이블 대신 Common IFdcTraceSource 계약으로만 TRACE를 소비해야 합니다");
    }

    [Fact]
    public void Reusable_framework_projects_do_not_reference_product_or_mes_projects()
    {
        var violations = new List<string>();

        foreach (var project in ReusableFrameworkProjects)
        {
            File.Exists(project).Should().BeTrue($"재사용 Framework 검사 대상이 존재해야 합니다: {project}");
            foreach (var reference in ReadDeclaredReferences(project))
            {
                if (IsForbiddenFrameworkProductReference(reference.Include))
                {
                    violations.Add(
                        $"{Path.GetFileNameWithoutExtension(project)} -> {reference.Include} ({reference.Kind})");
                }
            }
        }

        violations.Should().BeEmpty(
            "재사용 Framework는 NexaOne/NexusOne/NexaMes/MES 제품 의존을 흡수하지 않아야 합니다");
    }

    [Theory]
    [InlineData("../NexaOne.Server/NexaOne.Server.csproj", true)]
    [InlineData("NexusOne.QMS", true)]
    [InlineData("NexaMes.Integration, Version=1.0.0", true)]
    [InlineData("MES.Device.Client", true)]
    [InlineData("NexaFramework", false)]
    [InlineData("Microsoft.Extensions.Hosting.Abstractions", false)]
    [InlineData("Spring.Core", false)]
    public void Framework_product_reference_classifier_is_fail_closed_for_known_product_roots(
        string reference,
        bool expected)
    {
        IsForbiddenFrameworkProductReference(reference).Should().Be(expected);
    }

    private static IReadOnlyList<string> FindModuleDirectories() =>
        Directory.GetDirectories(ModulesRoot, "NexaOne.*", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> FindModuleProjects() =>
        FindModuleDirectories()
            .SelectMany(static directory => Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlySet<string> DiscoverPhysicalTables()
    {
        var migrations = Path.Combine(ServerRoot, "config", "db", "migrations");
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var createTable = new Regex(
            @"\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<table>[A-Z][A-Z0-9_]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var file in Directory.GetFiles(migrations, "*.sql", SearchOption.TopDirectoryOnly))
        {
            foreach (System.Text.RegularExpressions.Match match in createTable.Matches(File.ReadAllText(file)))
                tables.Add(match.Groups["table"].Value.ToUpperInvariant());
        }

        return tables;
    }

    private static IReadOnlyList<string> FindReferencedPhysicalTables(
        string source,
        IReadOnlySet<string> physicalTables)
    {
        var identifiers = Regex.Matches(
                source,
                @"\b[A-Z][A-Z0-9]*_[A-Z0-9_]+\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(static match => match.Value.ToUpperInvariant())
            .Where(physicalTables.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static table => table, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return identifiers;
    }

    private static string? GetPhysicalSchemaOwner(string table)
    {
        var separator = table.IndexOf('_');
        if (separator <= 0) return null;
        return PhysicalSchemaOwners.GetValueOrDefault(table[..separator]);
    }

    private static IReadOnlyList<DeclaredReference> ReadDeclaredReferences(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        return document.Descendants()
            .Where(static element => element.Name.LocalName is
                "ProjectReference" or "PackageReference" or "FrameworkReference" or "Reference")
            .Select(static element => new DeclaredReference(
                element.Name.LocalName,
                ((string?)element.Attribute("Include"))?.Trim() ?? string.Empty))
            .Where(static reference => reference.Include.Length > 0)
            .ToArray();
    }

    private static string ResolveProjectReference(string sourceProject, string include)
    {
        var platformPath = include
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceProject)!, platformPath));
    }

    private static bool IsForbiddenFrameworkProductReference(string reference)
    {
        var name = DependencyName(reference);
        return ProductAssemblyRoots.Any(root =>
            name.Equals(root, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static string DependencyName(string reference)
    {
        var normalized = reference.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..]
            .Split(',')[0]
            .Trim();
        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? name[..^".csproj".Length]
            : name;
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return PathsEqual(normalizedPath, normalizedRoot)
               || normalizedPath.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   PathComparison);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record DeclaredReference(string Kind, string Include);
    private sealed record ApprovedProjection(string SourcePath, string Table, string AdrPath);
}

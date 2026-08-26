using System.Xml.Linq;

namespace NexaOne.UnitTests.Architecture;

/// <summary>
/// 업무 Module이 제품 호스트나 다른 업무 Module의 implementation에 결합하지 않도록 project 의존을 검증합니다.
/// 재사용 Framework에도 같은 방향을 적용해 제품 참조가 Framework로 역류하는 것을 차단합니다.
/// </summary>
public sealed class ModuleDependencyBoundaryTests
{
    private static readonly string RepoRoot = RepositorySource.Root;
    private static readonly string ModulesRoot = Path.Combine(RepoRoot, "src", "04.Modules");
    private static readonly string ServerRoot = Path.Combine(RepoRoot, "src", "00.Main", "NexaOne.Server");

    private static readonly string[] ReusableFrameworkProjects =
    [
        Path.Combine(RepoRoot, "submodules", "NexusFramework", "src", "NexaFramework", "NexaFramework.csproj"),
        Path.Combine(RepoRoot, "submodules", "NexusFramework", "src", "NexaFramework.Hosting", "NexaFramework.Hosting.csproj"),
    ];

    private static readonly string[] ProductAssemblyRoots =
    [
        "NexaOne",
        "NexusOne",
        "NexaMes",
        "MES",
    ];

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
                    || source.Contains("using NexusLogic.Plc.", StringComparison.Ordinal))
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
        source.Should().Contain("GetBean(");
        source.Should().Contain("qmsProductionQualityGateway");
        source.Should().NotContain("QueryRepository");
        source.Should().NotContain("EesDataSource");
        source.Should().NotContain("QMS_");
        source.Should().NotContain("SELECT ");
        source.Should().NotContain("FROM ");
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
}

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NexaOne.Web.Services.Meta;
using RegexMatch = System.Text.RegularExpressions.Match;

namespace NexaOne.UnitTests.Assets;

/// <summary>
/// Source-owned database and screen assets form shared registries at runtime. These tests keep
/// collisions and ownership drift from being hidden by load order or dictionary overwrites.
/// </summary>
public sealed class AssetCollisionTests
{
    [Fact]
    public async Task Repository_assets_are_collision_free_and_respect_owner_boundaries()
    {
        var repositoryRoot = RepositorySource.Root;

        var queryCount = AssetCollisionGuard.ValidateQueries(repositoryRoot);
        var migrationCount = AssetCollisionGuard.ValidateMigrations(repositoryRoot);
        var providerSource = Path.Combine(
            repositoryRoot,
            "src",
            "01.Web",
            "NexaOne.Web.Components",
            "Services",
            "Meta",
            "InMemoryScreenDefinitionProvider.cs");
        var developmentSeedSource = Path.Combine(
            repositoryRoot,
            "src",
            "00.Main",
            "NexaOne.Server",
            "Hosting",
            "NexaOneDevelopmentDatabaseInitializer.cs");
        var screenScan = AssetCollisionGuard.ValidateScreenSeeds(providerSource, developmentSeedSource);

        queryCount.Should().BeGreaterThan(0, "the production query trees must not be skipped");
        migrationCount.Should().BeGreaterThan(0, "the production migration owner must not be skipped");
        screenScan.CanonicalIds.Should().NotBeEmpty("the code screen seed owners must not be skipped");
        screenScan.CanonicalIdsBySource[Path.GetFullPath(developmentSeedSource)].Should().NotBeEmpty(
            "development database screen seeds are part of the shared UI_ID namespace");

        // Static extraction must cover every canonical definition produced at runtime. Aliases
        // intentionally collapse to their target definition and therefore are not canonical seeds.
        var provider = new InMemoryScreenDefinitionProvider();
        var runtimeKeys = await provider.GetKnownUiIdsAsync();
        screenScan.AllIdsBySource[Path.GetFullPath(providerSource)].Should().BeEquivalentTo(runtimeKeys,
            "legacy alias keys and canonical provider keys share the same runtime dictionary");
        var runtimeCanonicalIds = new HashSet<string>(
            runtimeKeys
                .Select(key => provider.Get(key)?.UiId
                    ?? throw new InvalidDataException($"Screen alias '{key}' has no target definition.")),
            StringComparer.OrdinalIgnoreCase);

        screenScan.CanonicalIdsBySource[Path.GetFullPath(providerSource)].Should().BeEquivalentTo(runtimeCanonicalIds,
            "the source scanner must account for direct, helper-generated, and tuple-generated seeds");
    }

    [Fact]
    public void Duplicate_query_ids_in_one_dialect_fail_fast()
    {
        using var fixture = new AssetFixture();
        fixture.WriteQuery("queries", "sqlite", "MDM", "MDM.PlantList");
        fixture.WriteQuery("queries-auth", "sqlite", "MDM", "MDM.PlantList");

        var act = () => AssetCollisionGuard.ValidateQueries(fixture.Root);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*sqlite*MDM.PlantList*");
    }

    [Fact]
    public void Query_ids_cannot_escape_their_file_module_owner()
    {
        using var fixture = new AssetFixture();
        fixture.WriteQuery("queries", "mssql", "MDM", "QMS.PlantList");

        var act = () => AssetCollisionGuard.ValidateQueries(fixture.Root);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*QMS.PlantList*MDM*");
    }

    [Fact]
    public void Duplicate_numeric_migration_versions_fail_fast()
    {
        using var fixture = new AssetFixture();
        fixture.WriteMigration("V001__SYS_USER.sql");
        fixture.WriteMigration("V001__POM_LOT.sql");

        var act = () => AssetCollisionGuard.ValidateMigrations(fixture.Root);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*version 1*V001__POM_LOT.sql*V001__SYS_USER.sql*");
    }

    [Fact]
    public void Migration_versions_require_exactly_three_digits()
    {
        using var fixture = new AssetFixture();
        fixture.WriteMigration("V1__SYS_USER.sql");

        var act = () => AssetCollisionGuard.ValidateMigrations(fixture.Root);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*V1__SYS_USER.sql*V###__DESCRIPTION.sql*");
    }

    [Fact]
    public void Migration_assets_cannot_escape_the_central_source_owner()
    {
        using var fixture = new AssetFixture();
        fixture.WriteForeignMigration("v200__pom_local.SQL");

        var act = () => AssetCollisionGuard.ValidateMigrations(fixture.Root);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*v200__pom_local.SQL*config*db*migrations*");
    }

    [Fact]
    public void Duplicate_code_screen_seed_ui_ids_fail_fast_across_owner_files_and_declaration_styles()
    {
        using var fixture = new AssetFixture();
        var providerSource = fixture.WriteScreenSource(
            """
            public sealed class ExampleProvider
            {
                private const string DuplicateId = "DUPLICATE_UI";

                public ExampleProvider()
                {
                    Register(new ScreenDefinition(DuplicateId, "Direct"));
                }
            }
            """,
            "ProviderFixture.cs");
        var developmentSource = fixture.WriteScreenSource(
            """
            public static class DevelopmentFixture
            {
                private static readonly object[] Seeds =
                {
                    (UiId: "DUPLICATE_UI", Title: "Development")
                };
            }
            """,
            "DevelopmentFixture.cs");

        var act = () => AssetCollisionGuard.ValidateScreenSeeds(providerSource, developmentSource);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*DUPLICATE_UI*ProviderFixture.cs*DevelopmentFixture.cs*");
    }

    private static class AssetCollisionGuard
    {
        private static readonly string[] QueryTrees = ["queries", "queries-auth"];
        private static readonly string[] RequiredDialects = ["mssql", "sqlite"];
        private static readonly HashSet<string> SupportedDialects =
            new(RequiredDialects, StringComparer.OrdinalIgnoreCase);

        private static readonly Regex MigrationFileName = new(
            @"^V(?<version>[0-9]{3})__(?<description>[A-Z0-9]+(?:_[A-Z0-9]+)*)\.sql$",
            RegexOptions.CultureInvariant);

        private static readonly Regex MigrationCandidateFileName = new(
            @"^V[0-9]+__.*\.sql$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex DirectScreenSeed = new(
            @"Register\s*\(\s*new\s+ScreenDefinition\s*\(\s*""(?<id>[^""]+)""",
            RegexOptions.CultureInvariant);

        private static readonly Regex DirectScreenSeedReference = new(
            @"Register\s*\(\s*new\s+ScreenDefinition\s*\(\s*(?<reference>[A-Za-z_][A-Za-z0-9_]*)\b",
            RegexOptions.CultureInvariant);

        private static readonly Regex HelperScreenSeed = new(
            @"\b(?<helper>Register(?!Alias\b)[A-Z][A-Za-z0-9_]*)\s*\(\s*""(?<id>[^""]+)""",
            RegexOptions.CultureInvariant);

        private static readonly Regex HelperScreenSeedReference = new(
            @"\b(?<helper>Register(?!Alias\b)[A-Z][A-Za-z0-9_]*)\s*\(\s*(?<reference>[A-Za-z_][A-Za-z0-9_]*)\b",
            RegexOptions.CultureInvariant);

        private static readonly Regex AliasScreenSeed = new(
            @"\bRegisterAlias\s*\(\s*""(?<id>[^""]+)""",
            RegexOptions.CultureInvariant);

        private static readonly Regex AliasScreenSeedReference = new(
            @"\bRegisterAlias\s*\(\s*(?<reference>[A-Za-z_][A-Za-z0-9_]*)\b",
            RegexOptions.CultureInvariant);

        private static readonly Regex TupleScreenSeedLoop = new(
            @"foreach\s*\(\s*var\s*\(\s*uiId\b[^)]*\)\s*in\s*new\[\]\s*\{(?<items>.*?)\}\s*\)\s*Register\s*\(\s*new\s+ScreenDefinition\s*\(\s*uiId\b",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        private static readonly Regex TupleFirstString = new(
            @"\(\s*""(?<id>[^""]+)""",
            RegexOptions.CultureInvariant);

        private static readonly Regex NamedUiIdSeed = new(
            @"\(\s*UiId\s*:\s*""(?<id>[^""]+)""",
            RegexOptions.CultureInvariant);

        private static readonly Regex StringValue = new(
            @"\b(?:const\s+string|string|var)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*""(?<id>[^""]+)""\s*;",
            RegexOptions.CultureInvariant);

        public static int ValidateQueries(string repositoryRoot)
        {
            var dbRoot = Path.Combine(
                repositoryRoot,
                "src",
                "00.Main",
                "NexaOne.Server",
                "config",
                "db");
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;

            foreach (var tree in QueryTrees)
            {
                var treeRoot = Path.Combine(dbRoot, tree);
                if (!Directory.Exists(treeRoot))
                    throw new InvalidDataException($"Query asset owner root is missing: {Display(repositoryRoot, treeRoot)}");

                foreach (var requiredDialect in RequiredDialects)
                {
                    var dialectRoot = Path.Combine(treeRoot, requiredDialect);
                    if (!Directory.Exists(dialectRoot)
                        || !Directory.EnumerateFiles(dialectRoot, "*.xml", SearchOption.TopDirectoryOnly).Any())
                    {
                        throw new InvalidDataException(
                            $"Query asset owner '{tree}/{requiredDialect}' must contain at least one module XML file.");
                    }
                }

                foreach (var file in Directory.EnumerateFiles(treeRoot, "*.xml", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var relativeToTree = Path.GetRelativePath(treeRoot, file);
                    var segments = SplitPath(relativeToTree);
                    if (segments.Length != 2 || !SupportedDialects.Contains(segments[0]))
                    {
                        throw new InvalidDataException(
                            $"Query asset '{Display(repositoryRoot, file)}' must be owned directly by " +
                            $"{tree}/{{mssql|sqlite}}/{{MODULE}}.xml.");
                    }

                    var dialect = segments[0];
                    var fileModule = Path.GetFileNameWithoutExtension(file);
                    var document = XDocument.Load(file);
                    var root = document.Root;
                    if (root is null || !string.Equals(root.Name.LocalName, "queries", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Query asset '{Display(repositoryRoot, file)}' must have a <queries> root.");
                    }

                    var declaredModule = root.Attributes()
                        .SingleOrDefault(attribute => attribute.Name.LocalName == "module")?
                        .Value.Trim();
                    if (string.IsNullOrWhiteSpace(declaredModule)
                        || !string.Equals(fileModule, declaredModule, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Query asset '{Display(repositoryRoot, file)}' is owned by file module '{fileModule}', " +
                            $"but declares module '{declaredModule ?? "<missing>"}'.");
                    }

                    foreach (var query in root.Elements().Where(element => element.Name.LocalName == "query"))
                    {
                        var id = query.Attributes()
                            .SingleOrDefault(attribute => attribute.Name.LocalName == "id")?
                            .Value.Trim();
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            throw new InvalidDataException(
                                $"Query asset '{Display(repositoryRoot, file)}' contains a query without an ID.");
                        }

                        var separator = id.IndexOf('.');
                        var idModule = separator > 0 ? id[..separator] : string.Empty;
                        if (!string.Equals(idModule, declaredModule, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                $"Query ID '{id}' in '{Display(repositoryRoot, file)}' must belong to module " +
                                $"'{declaredModule}' (expected prefix '{declaredModule}.').");
                        }

                        var collisionKey = $"{dialect}\0{id}";
                        if (!seen.TryAdd(collisionKey, file))
                        {
                            throw new InvalidDataException(
                                $"Query ID collision in dialect '{dialect}': '{id}' is declared by both " +
                                $"'{Display(repositoryRoot, seen[collisionKey])}' and '{Display(repositoryRoot, file)}'.");
                        }

                        count++;
                    }
                }
            }

            return count;
        }

        public static int ValidateMigrations(string repositoryRoot)
        {
            var sourceRoot = Path.Combine(repositoryRoot, "src");
            var migrationRoot = Path.Combine(
                sourceRoot,
                "00.Main",
                "NexaOne.Server",
                "config",
                "db",
                "migrations");
            if (!Directory.Exists(migrationRoot))
                throw new InvalidDataException($"Migration owner root is missing: {Display(repositoryRoot, migrationRoot)}");

            var versions = new Dictionary<long, string>();
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                         .Where(path => string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase))
                         .Where(path => !IsGeneratedSourcePath(Path.GetRelativePath(sourceRoot, path)))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var isOwned = SamePath(Path.GetDirectoryName(file)!, migrationRoot);
                var fileName = Path.GetFileName(file);
                var match = MigrationFileName.Match(fileName);

                if (!isOwned)
                {
                    if (MigrationCandidateFileName.IsMatch(fileName))
                    {
                        throw new InvalidDataException(
                            $"Migration asset '{Display(repositoryRoot, file)}' is outside its sole source owner " +
                            $"'{Display(repositoryRoot, migrationRoot)}'.");
                    }

                    continue;
                }

                if (!match.Success)
                {
                    throw new InvalidDataException(
                        $"Migration asset '{Display(repositoryRoot, file)}' must match V###__DESCRIPTION.sql.");
                }

                if (!long.TryParse(
                        match.Groups["version"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var version)
                    || version <= 0)
                {
                    throw new InvalidDataException(
                        $"Migration asset '{Display(repositoryRoot, file)}' has an invalid positive numeric version.");
                }

                if (!versions.TryAdd(version, file))
                {
                    throw new InvalidDataException(
                        $"Migration version {version} is declared by both " +
                        $"'{Display(repositoryRoot, versions[version])}' and '{Display(repositoryRoot, file)}'.");
                }

                count++;
            }

            return count;
        }

        public static ScreenSeedScan ValidateScreenSeeds(params string[] sourcePaths)
        {
            if (sourcePaths.Length == 0)
                throw new InvalidDataException("At least one code screen seed owner must be supplied.");

            var candidates = new List<ScreenSeedCandidate>();
            var normalizedPaths = sourcePaths.Select(Path.GetFullPath).ToArray();
            for (var sourceOrder = 0; sourceOrder < normalizedPaths.Length; sourceOrder++)
            {
                var sourcePath = normalizedPaths[sourceOrder];
                if (!File.Exists(sourcePath))
                    throw new InvalidDataException($"Code screen seed source is missing: {sourcePath}");

                var source = File.ReadAllText(sourcePath);
                var region = GetScreenSeedRegion(sourcePath, source);
                var tupleLoops = TupleScreenSeedLoop.Matches(region.Text).Cast<RegexMatch>().ToArray();
                var stringValues = StringValue.Matches(source)
                    .Cast<RegexMatch>()
                    .Select(match => new StringValueCandidate(
                        match.Index,
                        match.Groups["name"].Value,
                        match.Groups["id"].Value))
                    .ToArray();
                var countBeforeSource = candidates.Count;

                AddLiteralMatches(
                    candidates,
                    DirectScreenSeed.Matches(region.Text),
                    sourceOrder,
                    sourcePath,
                    source,
                    region.Offset,
                    "direct",
                    canonical: true);
                AddLiteralMatches(
                    candidates,
                    HelperScreenSeed.Matches(region.Text),
                    sourceOrder,
                    sourcePath,
                    source,
                    region.Offset,
                    "helper",
                    canonical: true);
                AddLiteralMatches(
                    candidates,
                    AliasScreenSeed.Matches(region.Text),
                    sourceOrder,
                    sourcePath,
                    source,
                    region.Offset,
                    "alias",
                    canonical: false);
                AddLiteralMatches(
                    candidates,
                    NamedUiIdSeed.Matches(region.Text),
                    sourceOrder,
                    sourcePath,
                    source,
                    region.Offset,
                    "named tuple",
                    canonical: true);

                foreach (var loop in tupleLoops)
                {
                    var items = loop.Groups["items"];
                    foreach (RegexMatch tuple in TupleFirstString.Matches(items.Value))
                    {
                        var offset = region.Offset + items.Index + tuple.Index;
                        candidates.Add(new ScreenSeedCandidate(
                            sourceOrder,
                            sourcePath,
                            offset,
                            LineNumber(source, offset),
                            tuple.Groups["id"].Value,
                            "tuple loop",
                            IsCanonical: true));
                    }
                }

                foreach (RegexMatch reference in DirectScreenSeedReference.Matches(region.Text))
                {
                    if (tupleLoops.Any(loop =>
                            reference.Index >= loop.Index && reference.Index < loop.Index + loop.Length))
                    {
                        continue;
                    }

                    AddReferenceMatch(
                        candidates,
                        reference,
                        stringValues,
                        sourceOrder,
                        sourcePath,
                        source,
                        region.Offset,
                        "direct reference",
                        canonical: true);
                }

                foreach (RegexMatch reference in HelperScreenSeedReference.Matches(region.Text))
                {
                    AddReferenceMatch(
                        candidates,
                        reference,
                        stringValues,
                        sourceOrder,
                        sourcePath,
                        source,
                        region.Offset,
                        "helper reference",
                        canonical: true);
                }

                foreach (RegexMatch reference in AliasScreenSeedReference.Matches(region.Text))
                {
                    AddReferenceMatch(
                        candidates,
                        reference,
                        stringValues,
                        sourceOrder,
                        sourcePath,
                        source,
                        region.Offset,
                        "alias reference",
                        canonical: false);
                }

                if (candidates.Count == countBeforeSource)
                    throw new InvalidDataException($"No code screen seeds were discovered in '{sourcePath}'.");
            }

            candidates.Sort((left, right) =>
            {
                var sourceComparison = left.SourceOrder.CompareTo(right.SourceOrder);
                return sourceComparison != 0 ? sourceComparison : left.Offset.CompareTo(right.Offset);
            });

            var seen = new Dictionary<string, ScreenSeedCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (!seen.TryAdd(candidate.Id, candidate))
                {
                    var first = seen[candidate.Id];
                    throw new InvalidDataException(
                        $"Code screen seed UI_ID collision: '{candidate.Id}' is declared at " +
                        $"'{first.SourcePath}' line {first.Line} ({first.Style}) and " +
                        $"'{candidate.SourcePath}' line {candidate.Line} ({candidate.Style}).");
                }
            }

            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var idsBySource = new Dictionary<string, IReadOnlyList<string>>(pathComparer);
            var allIdsBySource = new Dictionary<string, IReadOnlyList<string>>(pathComparer);
            foreach (var sourcePath in normalizedPaths)
            {
                idsBySource[sourcePath] = candidates
                    .Where(candidate => candidate.IsCanonical && pathComparer.Equals(candidate.SourcePath, sourcePath))
                    .Select(candidate => candidate.Id)
                    .ToArray();
                allIdsBySource[sourcePath] = candidates
                    .Where(candidate => pathComparer.Equals(candidate.SourcePath, sourcePath))
                    .Select(candidate => candidate.Id)
                    .ToArray();
            }

            return new ScreenSeedScan(
                candidates.Where(candidate => candidate.IsCanonical).Select(candidate => candidate.Id).ToArray(),
                idsBySource,
                allIdsBySource);
        }

        private static void AddLiteralMatches(
            ICollection<ScreenSeedCandidate> candidates,
            MatchCollection matches,
            int sourceOrder,
            string sourcePath,
            string source,
            int regionOffset,
            string style,
            bool canonical)
        {
            foreach (RegexMatch match in matches)
            {
                var offset = regionOffset + match.Index;
                candidates.Add(new ScreenSeedCandidate(
                    sourceOrder,
                    sourcePath,
                    offset,
                    LineNumber(source, offset),
                    match.Groups["id"].Value,
                    style,
                    canonical));
            }
        }

        private static void AddReferenceMatch(
            ICollection<ScreenSeedCandidate> candidates,
            RegexMatch match,
            IReadOnlyList<StringValueCandidate> stringValues,
            int sourceOrder,
            string sourcePath,
            string source,
            int regionOffset,
            string style,
            bool canonical)
        {
            var referenceName = match.Groups["reference"].Value;
            var absoluteOffset = regionOffset + match.Index;
            var possibleValues = stringValues
                .Where(value => string.Equals(value.Name, referenceName, StringComparison.Ordinal))
                .ToArray();
            var resolved = possibleValues
                .Where(value => value.Offset < absoluteOffset)
                .OrderByDescending(value => value.Offset)
                .FirstOrDefault()
                ?? (possibleValues.Length == 1 ? possibleValues[0] : null);
            if (resolved is null)
            {
                throw new InvalidDataException(
                    $"Unsupported code screen seed reference '{referenceName}' at '{sourcePath}' " +
                    $"line {LineNumber(source, absoluteOffset)}. Use a literal or a single string/const assignment.");
            }

            candidates.Add(new ScreenSeedCandidate(
                sourceOrder,
                sourcePath,
                absoluteOffset,
                LineNumber(source, absoluteOffset),
                resolved.Id,
                style,
                canonical));
        }

        private static ScreenSeedRegion GetScreenSeedRegion(string sourcePath, string source)
        {
            if (!string.Equals(
                    Path.GetFileName(sourcePath),
                    "InMemoryScreenDefinitionProvider.cs",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new ScreenSeedRegion(source, 0);
            }

            const string constructorSignature = "public InMemoryScreenDefinitionProvider()";
            const string nextMemberSignature = "    public void Register(";
            var start = source.IndexOf(constructorSignature, StringComparison.Ordinal);
            var end = start < 0
                ? -1
                : source.IndexOf(nextMemberSignature, start, StringComparison.Ordinal);
            if (start < 0 || end <= start)
            {
                throw new InvalidDataException(
                    $"Could not isolate the code screen seed constructor in '{sourcePath}'.");
            }

            return new ScreenSeedRegion(source[start..end], start);
        }

        private static int LineNumber(string source, int offset)
        {
            var line = 1;
            for (var index = 0; index < offset; index++)
            {
                if (source[index] == '\n') line++;
            }

            return line;
        }

        private static bool IsGeneratedSourcePath(string relativePath)
            => SplitPath(relativePath).Any(segment =>
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));

        private static string[] SplitPath(string path)
            => path.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

        private static bool SamePath(string left, string right)
            => string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static string Display(string repositoryRoot, string path)
            => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');

        public sealed record ScreenSeedScan(
            IReadOnlyList<string> CanonicalIds,
            IReadOnlyDictionary<string, IReadOnlyList<string>> CanonicalIdsBySource,
            IReadOnlyDictionary<string, IReadOnlyList<string>> AllIdsBySource);

        private sealed record ScreenSeedCandidate(
            int SourceOrder,
            string SourcePath,
            int Offset,
            int Line,
            string Id,
            string Style,
            bool IsCanonical);

        private sealed record ScreenSeedRegion(string Text, int Offset);

        private sealed record StringValueCandidate(int Offset, string Name, string Id);
    }

    private sealed class AssetFixture : IDisposable
    {
        private static readonly string DbRelativePath = Path.Combine(
            "src",
            "00.Main",
            "NexaOne.Server",
            "config",
            "db");

        public AssetFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"nexa-asset-collision-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(Root, DbRelativePath, "queries"));
            Directory.CreateDirectory(Path.Combine(Root, DbRelativePath, "queries-auth"));
            Directory.CreateDirectory(Path.Combine(Root, DbRelativePath, "migrations"));
            WriteQuery("queries", "mssql", "SYS", "SYS.PublicMssqlFixture");
            WriteQuery("queries", "sqlite", "SYS", "SYS.PublicSqliteFixture");
            WriteQuery("queries-auth", "mssql", "SYS", "SYS.AuthMssqlFixture");
            WriteQuery("queries-auth", "sqlite", "SYS", "SYS.AuthSqliteFixture");
        }

        public string Root { get; }

        public void WriteQuery(string tree, string dialect, string module, string id)
        {
            var directory = Path.Combine(Root, DbRelativePath, tree, dialect);
            Directory.CreateDirectory(directory);
            new XDocument(
                new XElement(
                    "queries",
                    new XAttribute("module", module),
                    new XElement("query", new XAttribute("id", id))))
                .Save(Path.Combine(directory, $"{module}.xml"));
        }

        public void WriteMigration(string fileName)
            => File.WriteAllText(Path.Combine(Root, DbRelativePath, "migrations", fileName), "SELECT 1;");

        public void WriteForeignMigration(string fileName)
        {
            var directory = Path.Combine(Root, "src", "04.Modules", "NexaOne.POM", "migrations");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), "SELECT 1;");
        }

        public string WriteScreenSource(string source, string fileName = "ScreenSeedFixture.cs")
        {
            var directory = Path.Combine(
                Root,
                "src",
                "01.Web",
                "NexaOne.Web.Components",
                "Services",
                "Meta");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            File.WriteAllText(path, source);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of a uniquely named test directory.
            }
        }
    }
}

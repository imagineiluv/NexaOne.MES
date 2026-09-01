using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class ProductPackagingProfileTests
{
    [Fact]
    public void Cleaner_is_the_default_and_every_profile_manifest_matches_its_declared_plugin_projects()
    {
        var profileRoot = RepositorySource.GetDirectory("eng/product-profiles");
        var selector = XDocument.Load(Path.Combine(profileRoot, "NexaOne.ProductProfiles.props"));
        selector.Descendants("NexaOneProductProfile")
            .Single(property => property.Attribute("Condition") is not null)
            .Value.Should().Be("Cleaner");

        var corePlugins = ReadPlugins(Path.Combine(profileRoot, "Core.props"));
        var profiles = Directory.EnumerateFiles(
                Path.Combine(profileRoot, "profiles"), "*.props", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToList();
        profiles.Select(Path.GetFileNameWithoutExtension)
            .Should().Contain(["Cleaner", "PomOnly"]);

        foreach (var profilePath in profiles)
        {
            var profile = XDocument.Load(profilePath);
            var name = RequiredProperty(profile, "NexaOneProductProfileName");
            name.Should().Be(Path.GetFileNameWithoutExtension(profilePath));

            var plugins = corePlugins.Concat(ReadPlugins(profilePath)).ToList();
            plugins.Select(static plugin => plugin.AssemblyName)
                .Should().OnlyHaveUniqueItems($"profile '{name}' must be an exact file-set declaration");
            foreach (var plugin in plugins)
            {
                File.Exists(plugin.ProjectPath).Should().BeTrue(
                    $"profile '{name}' project must exist: {plugin.ProjectPath}");
            }

            var manifestExpression = RequiredProperty(profile, "NexaOneProductApplicationManifestSource");
            var manifestPath = ResolveMsBuildPath(manifestExpression, Path.GetDirectoryName(profilePath)!);
            File.Exists(manifestPath).Should().BeTrue($"profile '{name}' manifest must exist");

            var evaluated = EvaluateProfile(name);
            Path.GetFullPath(evaluated.ManifestPath).Should().Be(
                Path.GetFullPath(manifestPath),
                $"profile '{name}' must be selectable through the shared MSBuild import");
            evaluated.PluginAssemblyNames.Should().BeEquivalentTo(
                plugins.Select(static plugin => plugin.AssemblyName),
                $"profile '{name}' evaluation must preserve its declaration's exact plugin set");

            var manifest = XDocument.Load(manifestPath);
            var manifestPlugins = manifest.Root!
                .Element("Services")!
                .Elements("Service")
                .SelectMany(service => ((string?)service.Attribute("classPaths") ?? string.Empty)
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(Path.GetFileNameWithoutExtension)
                .ToHashSet(PathComparer);
            manifestPlugins.Should().BeEquivalentTo(
                plugins.Select(static plugin => plugin.AssemblyName),
                $"profile '{name}' is the declaration source for both build and Spring classPaths");
        }
    }

    [Fact]
    public void Host_and_smoke_projects_consume_the_profile_item_instead_of_fixed_project_catalogs()
    {
        var server = XDocument.Load(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "NexaOne.Server.csproj"));
        var tests = XDocument.Load(RepositorySource.GetFile(
            "test", "NexaOne.ServerTests", "NexaOne.ServerTests.csproj"));

        server.Descendants("ProjectReference")
            .Should().ContainSingle(reference => (string?)reference.Attribute("Include") == "@(NexaOneProductPlugin)");
        tests.Descendants("ProjectReference")
            .Should().ContainSingle(reference => (string?)reference.Attribute("Include") == "@(NexaOneProductPlugin)");
        server.Descendants("ProjectReference")
            .Concat(tests.Descendants("ProjectReference"))
            .Select(reference => (string?)reference.Attribute("Include") ?? string.Empty)
            .Should().NotContain(include =>
                include.Contains("04.Modules", StringComparison.Ordinal)
                || include.Contains("05.Projects", StringComparison.Ordinal));

        var selectedManifest = server.Descendants("Content").Single(content =>
            (string?)content.Attribute("Include") == "$(NexaOneProductApplicationManifestSource)");
        selectedManifest.Element("CopyToOutputDirectory")!.Value.Should().Be("Always");
        selectedManifest.Element("CopyToPublishDirectory")!.Value.Should().Be("Always");

        var smokeSource = File.ReadAllText(RepositorySource.GetFile(
            "test", "NexaOne.ServerTests", "HostModulesBootSmokeTests.cs"));
        smokeSource.Should().NotContain("ExpectedPluginModuleNames")
            .And.NotContain("12-assembly catalog")
            .And.Contain("product-profile.manifest");
    }

    [Fact]
    public void Release_verifier_preserves_pre_profile_Cleaner_manifests()
    {
        File.ReadAllText(RepositorySource.GetFile("tools", "ops", "Publish-ReleaseBundle.ps1"))
            .Should().Contain("packagingProfile = $ProductProfile",
                "every newly published release manifest must persist the selected profile");

        var repository = Path.Combine(Path.GetTempPath(), "nexa-release-profile-" + Guid.NewGuid().ToString("N"));
        const string version = "1.2.3";
        var release = Path.Combine(repository, "release", version);
        Directory.CreateDirectory(Path.Combine(release, "artifacts"));
        Directory.CreateDirectory(Path.Combine(release, "dll"));
        var bundle = Path.Combine(release, "artifacts", $"NexaMES.{version}.zip");
        var assembly = Path.Combine(release, "dll", "NexaOne.Server.dll");
        File.WriteAllText(bundle, "bundle");
        File.WriteAllText(assembly, "assembly");

        try
        {
            WriteReleaseManifest(release, version, bundle, assembly, packagingProfile: null);
            var legacy = RunReleaseVerifier(repository, version);
            legacy.ExitCode.Should().Be(0, legacy.Output);
            legacy.Output.Should().Contain("Cleaner (legacy manifest default)");
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    [Fact]
    public void Current_release_verifier_binds_profile_catalog_modules_and_application_manifest()
    {
        var repository = Path.Combine(Path.GetTempPath(), "nexa-release-profile-" + Guid.NewGuid().ToString("N"));
        const string version = "1.2.4";
        var release = Path.Combine(repository, "release", version);
        Directory.CreateDirectory(Path.Combine(release, "artifacts"));
        Directory.CreateDirectory(Path.Combine(release, "dll"));
        var bundle = Path.Combine(release, "artifacts", $"NexaMES.{version}.zip");
        var assembly = Path.Combine(release, "dll", "NexaOne.Server.dll");
        File.WriteAllText(assembly, "assembly");

        try
        {
            WriteProductBundle(
                bundle,
                profile: "PomOnly",
                declaredPlugins: ["NexaOne.POM"],
                modulePlugins: ["NexaOne.POM"],
                applicationPlugins: ["NexaOne.POM"]);
            WriteReleaseManifest(release, version, bundle, assembly, packagingProfile: "PomOnly");
            var current = RunReleaseVerifier(repository, version);
            current.ExitCode.Should().Be(0, current.Output);
            current.Output.Should().Contain("profile PomOnly");

            WriteProductBundle(
                bundle,
                profile: "Cleaner",
                declaredPlugins: ["NexaOne.POM"],
                modulePlugins: ["NexaOne.POM"],
                applicationPlugins: ["NexaOne.POM"]);
            WriteReleaseManifest(release, version, bundle, assembly, packagingProfile: "PomOnly");
            var profileMismatch = RunReleaseVerifier(repository, version);
            profileMismatch.ExitCode.Should().NotBe(0);
            profileMismatch.Output.Should().Contain("packagingProfile does not match");
            profileMismatch.Output.Should().Contain("manifest=PomOnly, bundle=Cleaner");

            WriteProductBundle(
                bundle,
                profile: "PomOnly",
                declaredPlugins: ["NexaOne.POM"],
                modulePlugins: ["NexaOne.POM"],
                applicationPlugins: ["NexaOne.POM"],
                additionalEntryPaths: ["Modules/NexaOne.POM.dll"]);
            WriteReleaseManifest(release, version, bundle, assembly, packagingProfile: "PomOnly");
            var duplicateEntry = RunReleaseVerifier(repository, version);
            duplicateEntry.ExitCode.Should().NotBe(0);
            duplicateEntry.Output.Should().Contain("duplicate or case-colliding ZIP entry path");

            WriteProductBundle(
                bundle,
                profile: "PomOnly",
                declaredPlugins: ["NexaOne.POM"],
                modulePlugins: ["NexaOne.POM"],
                applicationPlugins: ["NexaOne.POM"],
                additionalEntryPaths: ["modules/NexaOne.POM.dll"]);
            WriteReleaseManifest(release, version, bundle, assembly, packagingProfile: "PomOnly");
            var caseCollidingEntry = RunReleaseVerifier(repository, version);
            caseCollidingEntry.ExitCode.Should().NotBe(0);
            caseCollidingEntry.Output.Should().Contain("duplicate or case-colliding ZIP entry path");

            WriteProductBundle(
                bundle,
                profile: "PomOnly",
                declaredPlugins: ["NexaOne.POM"],
                modulePlugins: ["NexaOne.POM", "NexaOne.Project.Cleaner"],
                applicationPlugins: ["NexaOne.POM"]);
            WriteReleaseManifest(release, version, bundle, assembly, packagingProfile: "PomOnly");
            var moduleMismatch = RunReleaseVerifier(repository, version);
            moduleMismatch.ExitCode.Should().NotBe(0);
            moduleMismatch.Output.Should().Contain("Modules file-set does not match");

            WriteProductBundle(
                bundle,
                profile: "PomOnly",
                declaredPlugins: ["NexaOne.POM"],
                modulePlugins: [],
                applicationPlugins: ["NexaOne.POM"]);
            WriteReleaseManifest(release, version, bundle, assembly, packagingProfile: "PomOnly");
            var missingModule = RunReleaseVerifier(repository, version);
            missingModule.ExitCode.Should().NotBe(0);
            missingModule.Output.Should().Contain("Modules file-set does not match");

            WriteProductBundle(
                bundle,
                profile: "PomOnly",
                declaredPlugins: ["NexaOne.POM"],
                modulePlugins: ["NexaOne.POM"],
                applicationPlugins: ["NexaOne.POM", "NexaOne.Project.Cleaner"]);
            WriteReleaseManifest(release, version, bundle, assembly, packagingProfile: "PomOnly");
            var applicationMismatch = RunReleaseVerifier(repository, version);
            applicationMismatch.ExitCode.Should().NotBe(0);
            applicationMismatch.Output.Should().Contain("config/app.xml plugin set does not match");

            WriteProductBundle(
                bundle,
                profile: "PomOnly",
                declaredPlugins: ["NexaOne.POM"],
                modulePlugins: ["NexaOne.POM"],
                applicationPlugins: ["NexaOne.POM"]);
            WriteReleaseManifest(release, version, bundle, assembly, packagingProfile: "invalid/profile");
            var invalidProfile = RunReleaseVerifier(repository, version);
            invalidProfile.ExitCode.Should().NotBe(0);
            invalidProfile.Output.Should().Contain("packagingProfile is invalid");
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    private static void WriteReleaseManifest(
        string release,
        string version,
        string bundle,
        string assembly,
        string? packagingProfile)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["product"] = "NexaMES",
            ["version"] = version,
            ["configuration"] = "Release",
            ["commit"] = new string('a', 40),
            ["submodules"] = new Dictionary<string, string>
            {
                ["NexaFramework"] = new string('b', 40),
                ["NexaDB"] = new string('c', 40),
                ["NexaLogic"] = new string('d', 40),
            },
            ["bundle"] = new
            {
                path = $"artifacts/NexaMES.{version}.zip",
                fileSize = new FileInfo(bundle).Length,
                sha256 = Sha256(bundle),
            },
            ["managedDlls"] = new[]
            {
                new
                {
                    fileName = "NexaOne.Server.dll",
                    relativePath = "dll/NexaOne.Server.dll",
                    bytes = new FileInfo(assembly).Length,
                    sha256 = Sha256(assembly),
                },
            },
        };
        if (packagingProfile is not null)
            manifest["packagingProfile"] = packagingProfile;

        File.WriteAllText(
            Path.Combine(release, "release-manifest.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static void WriteProductBundle(
        string bundle,
        string profile,
        IReadOnlyCollection<string> declaredPlugins,
        IReadOnlyCollection<string> modulePlugins,
        IReadOnlyCollection<string> applicationPlugins,
        IReadOnlyCollection<string>? additionalEntryPaths = null)
    {
        File.Delete(bundle);
        using var archive = ZipFile.Open(bundle, ZipArchiveMode.Create);
        WriteZipEntry(
            archive,
            "config/product-profile.manifest",
            string.Join("\n", new[]
            {
                "FormatVersion=1",
                $"Profile={profile}",
                "ApplicationManifest=config/app.xml",
            }.Concat(declaredPlugins.Select(static plugin => $"Plugin={plugin}"))) + '\n');
        WriteZipEntry(
            archive,
            "config/app.xml",
            "<Application><Services>" + string.Concat(applicationPlugins.Select((plugin, index) =>
                $"<Service name=\"Plugin{index}\" classPaths=\"./Modules/{plugin}.dll\" />")) +
            "</Services></Application>");
        foreach (var plugin in modulePlugins)
            WriteZipEntry(archive, $"Modules/{plugin}.dll", $"binary:{plugin}");
        foreach (var path in additionalEntryPaths ?? [])
            WriteZipEntry(archive, path, $"additional:{path}");
    }

    private static void WriteZipEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(
            entry.Open(),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static (int ExitCode, string Output) RunReleaseVerifier(string repository, string version)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(RepositorySource.GetFile("tools", "ops", "Verify-ReleaseBundle.ps1"));
        startInfo.ArgumentList.Add("-Version");
        startInfo.ArgumentList.Add(version);
        startInfo.ArgumentList.Add("-RepositoryRoot");
        startInfo.ArgumentList.Add(repository);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the release verifier.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        var output = standardOutput.Result + Environment.NewLine + standardError.Result;
        return (process.ExitCode, NormalizeProcessOutput(output));
    }

    private static string NormalizeProcessOutput(string value)
    {
        var withoutAnsi = Regex.Replace(value, "\\x1B\\[[0-?]*[ -/]*[@-~]", string.Empty);
        return Regex.Replace(withoutAnsi, "\\s+", " ").Trim();
    }

    private static EvaluatedProfile EvaluateProfile(string profile)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(RepositorySource.GetFile(
            "src", "00.Main", "NexaOne.Server", "NexaOne.Server.csproj"));
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add($"-p:NexaOneProductProfile={profile}");
        startInfo.ArgumentList.Add("-getProperty:NexaOneProductProfileName");
        startInfo.ArgumentList.Add("-getProperty:NexaOneProductApplicationManifestSource");
        startInfo.ArgumentList.Add("-getItem:NexaOneProductPlugin");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to evaluate the product profile.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(standardOutput, standardError);
        process.ExitCode.Should().Be(0, standardError.Result);

        using var document = JsonDocument.Parse(standardOutput.Result);
        var root = document.RootElement;
        root.GetProperty("Properties").GetProperty("NexaOneProductProfileName").GetString()
            .Should().Be(profile);
        var manifest = root.GetProperty("Properties")
            .GetProperty("NexaOneProductApplicationManifestSource")
            .GetString()!;
        var pluginNames = root.GetProperty("Items").GetProperty("NexaOneProductPlugin")
            .EnumerateArray()
            .Select(item => item.GetProperty("AssemblyName").GetString()!)
            .ToList();
        return new EvaluatedProfile(manifest, pluginNames.AsReadOnly());
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static IReadOnlyList<PluginDeclaration> ReadPlugins(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        return XDocument.Load(path)
            .Descendants("NexaOneProductPlugin")
            .Select(item => new PluginDeclaration(
                ResolveMsBuildPath((string)item.Attribute("Include")!, directory),
                (string)item.Attribute("AssemblyName")!))
            .ToList();
    }

    private static string RequiredProperty(XDocument document, string name) =>
        document.Descendants(name).Single().Value.Trim();

    private static string ResolveMsBuildPath(string expression, string declaringDirectory)
    {
        var expanded = expression.Replace(
            "$(MSBuildThisFileDirectory)",
            declaringDirectory + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);
        return Path.GetFullPath(expanded
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar));
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record PluginDeclaration(string ProjectPath, string AssemblyName);

    private sealed record EvaluatedProfile(string ManifestPath, IReadOnlyList<string> PluginAssemblyNames);
}

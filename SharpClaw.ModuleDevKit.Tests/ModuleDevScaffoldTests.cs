using System.Text.Json;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.ModuleDev.Services;

namespace SharpClaw.ModuleDevKit.Tests;

[TestFixture]
public sealed class ModuleDevScaffoldTests
{
    private string _externalModulesDir = null!;

    [SetUp]
    public void SetUp()
    {
        _externalModulesDir = Path.Combine(
            Path.GetTempPath(),
            "SharpClawModuleDevScaffoldTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_externalModulesDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_externalModulesDir))
            Directory.Delete(_externalModulesDir, recursive: true);
    }

    [Test]
    public async Task ScaffoldAsync_WhenRuntimeIsDotNet_UsesContractsPackageReference()
    {
        var sut = CreateSut();

        var result = await sut.ScaffoldAsync(new ModuleScaffoldService.ScaffoldSpec(
            ModuleId: "sample_dotnet",
            DisplayName: "Sample Dotnet",
            ToolPrefix: "sd"));

        var manifestText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "module.json"));
        using var manifest = JsonDocument.Parse(manifestText);
        var projectText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "SampleDotnet.csproj"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Files, Is.EqualTo(new[]
            {
                "SampleDotnet.csproj",
                "SampleDotnetModule.cs",
                "module.json"
            }));
            Assert.That(manifest.RootElement.GetProperty("runtime").GetString(), Is.EqualTo("dotnet"));
            Assert.That(manifest.RootElement.GetProperty("entryAssembly").GetString(), Is.EqualTo("SampleDotnet.dll"));
            Assert.That(projectText, Does.Contain("<PackageReference Include=\"SharpClaw.Contracts\" />"));
            Assert.That(projectText, Does.Not.Contain("<HintPath>"));
        });
    }

    [Test]
    public async Task WriteFileAsync_AllowsDotNetModuleFiles()
    {
        var lifecycle = new FakeLifecycleManager(_externalModulesDir);
        var workspace = new ModuleWorkspaceService(lifecycle);

        var sourceFile = await workspace.WriteFileAsync("sample_module", "SampleModule.cs", "public sealed class SampleModule {}");
        var projectFile = await workspace.WriteFileAsync("sample_module", "SampleModule.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var manifestFile = await workspace.WriteFileAsync("sample_module", "module.json", ValidManifest);
        var settingsFile = await workspace.WriteFileAsync("sample_module", "settings.json", "{}");
        var readmeFile = await workspace.WriteFileAsync("sample_module", "README.md", "Module notes");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(sourceFile.Path), Is.True);
            Assert.That(File.Exists(projectFile.Path), Is.True);
            Assert.That(File.Exists(manifestFile.Path), Is.True);
            Assert.That(File.Exists(settingsFile.Path), Is.True);
            Assert.That(File.Exists(readmeFile.Path), Is.True);
        });
    }

    [Test]
    public void WriteFileAsync_RejectsNonDotNetManifestBeforeWriting()
    {
        var lifecycle = new FakeLifecycleManager(_externalModulesDir);
        var workspace = new ModuleWorkspaceService(lifecycle);
        var moduleDir = Path.Combine(_externalModulesDir, "sample_module");
        const string manifest = """
            {
              "id": "sample_module",
              "displayName": "Sample Module",
              "version": "0.1.0-beta",
              "toolPrefix": "sm",
              "runtime": "node",
              "entrypoint": "index.mjs",
              "minHostVersion": "0.1.0-beta"
            }
            """;

        var exception = Assert.ThrowsAsync<NotSupportedException>(async () =>
            await workspace.WriteFileAsync("sample_module", "module.json", manifest));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("only supports 'dotnet' modules"));
            Assert.That(Directory.Exists(moduleDir), Is.False);
        });
    }

    [Test]
    public void WriteFileAsync_RejectsInvalidManifestBeforeWriting()
    {
        var lifecycle = new FakeLifecycleManager(_externalModulesDir);
        var workspace = new ModuleWorkspaceService(lifecycle);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await workspace.WriteFileAsync("sample_module", "module.json", "{"));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("not valid JSON"));
            Assert.That(File.Exists(Path.Combine(_externalModulesDir, "sample_module", "module.json")), Is.False);
        });
    }

    [Test]
    public void WriteFileAsync_RejectsManifestWithoutDllEntryAssemblyBeforeWriting()
    {
        var lifecycle = new FakeLifecycleManager(_externalModulesDir);
        var workspace = new ModuleWorkspaceService(lifecycle);
        const string manifest = """
            {
              "id": "sample_module",
              "displayName": "Sample Module",
              "version": "0.1.0-beta",
              "toolPrefix": "sm",
              "runtime": "dotnet",
              "entryAssembly": "SampleModule.exe",
              "minHostVersion": "0.1.0-beta"
            }
            """;

        var exception = Assert.ThrowsAsync<ArgumentException>(async () =>
            await workspace.WriteFileAsync("sample_module", "module.json", manifest));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("extension '.dll'"));
            Assert.That(File.Exists(Path.Combine(_externalModulesDir, "sample_module", "module.json")), Is.False);
        });
    }

    private const string ValidManifest = """
        {
          "id": "sample_module",
          "displayName": "Sample Module",
          "version": "0.1.0-beta",
          "toolPrefix": "sm",
          "runtime": "dotnet",
          "entryAssembly": "SampleModule.dll",
          "minHostVersion": "0.1.0-beta"
        }
        """;

    private ModuleScaffoldService CreateSut()
    {
        var lifecycle = new FakeLifecycleManager(_externalModulesDir);
        var workspace = new ModuleWorkspaceService(lifecycle);
        var devEnvironment = new DevEnvironmentService(new FakeModuleInfoProvider(), lifecycle);
        return new ModuleScaffoldService(workspace, devEnvironment, lifecycle);
    }

    private sealed class FakeModuleInfoProvider : IModuleInfoProvider
    {
        public IReadOnlyList<ModuleInfo> GetAllModules() => [];
    }

    private sealed class FakeLifecycleManager(string externalModulesDir) : IModuleLifecycleManager
    {
        public string ExternalModulesDir { get; } = externalModulesDir;

        public bool IsModuleRegistered(string moduleId) => false;

        public bool IsToolPrefixRegistered(string toolPrefix) => false;

        public (ISharpClawCoreModule Module, string ToolName)? FindToolByName(string toolName) => null;

        public Task<ModuleStateResponse> LoadExternalAsync(
            string moduleDir,
            IServiceProvider hostServices,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnloadExternalAsync(string moduleId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ModuleStateResponse> ReloadExternalAsync(
            string moduleId,
            IServiceProvider hostServices,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}

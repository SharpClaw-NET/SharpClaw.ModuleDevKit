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
    public async Task ScaffoldAsync_WhenRuntimeIsNode_WritesNodeHostFiles()
    {
        var sut = CreateSut();

        var result = await sut.ScaffoldAsync(new ModuleScaffoldService.ScaffoldSpec(
            ModuleId: "sample_node",
            DisplayName: "Sample Node",
            ToolPrefix: "sn",
            Description: "A JavaScript module.",
            Runtime: "node"));

        var manifestText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "module.json"));
        using var manifest = JsonDocument.Parse(manifestText);
        var moduleText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "module.mjs"));
        var packageText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "package.json"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Files, Is.EqualTo(new[] { "package.json", "module.mjs", "module.json" }));
            Assert.That(File.Exists(Path.Combine(result.ModuleDir, "SampleNode.csproj")), Is.False);
            Assert.That(manifest.RootElement.GetProperty("runtime").GetString(), Is.EqualTo("node"));
            Assert.That(manifest.RootElement.GetProperty("entrypoint").GetString(), Is.EqualTo("module.mjs"));
            Assert.That(manifest.RootElement.GetProperty("entryAssembly").GetString(), Is.Empty);
            Assert.That(moduleText, Does.Contain("createSharpClawHost"));
            Assert.That(moduleText, Does.Contain("/modules/sample_node/ping"));
            Assert.That(moduleText, Does.Contain("storageContracts: []"));
            Assert.That(packageText, Does.Contain("\"@sharpclaw/module-host\": \"0.1.0-beta\""));
        });
    }

    [Test]
    public async Task ScaffoldAsync_WhenRuntimeIsPython_WritesPythonHostFiles()
    {
        var sut = CreateSut();

        var result = await sut.ScaffoldAsync(new ModuleScaffoldService.ScaffoldSpec(
            ModuleId: "sample_python",
            DisplayName: "Sample Python",
            ToolPrefix: "sp",
            Description: "A Python module.",
            Runtime: "python"));

        var manifestText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "module.json"));
        using var manifest = JsonDocument.Parse(manifestText);
        var moduleText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "module.py"));
        var projectText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "pyproject.toml"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Files, Is.EqualTo(new[] { "pyproject.toml", "module.py", "module.json" }));
            Assert.That(File.Exists(Path.Combine(result.ModuleDir, "SamplePython.csproj")), Is.False);
            Assert.That(manifest.RootElement.GetProperty("runtime").GetString(), Is.EqualTo("python"));
            Assert.That(manifest.RootElement.GetProperty("entrypoint").GetString(), Is.EqualTo("module.py"));
            Assert.That(manifest.RootElement.GetProperty("entryAssembly").GetString(), Is.Empty);
            Assert.That(moduleText, Does.Contain("create_sharpclaw_host"));
            Assert.That(moduleText, Does.Contain("/modules/sample_python/ping"));
            Assert.That(moduleText, Does.Contain("storage_contracts=[]"));
            Assert.That(projectText, Does.Contain("sharpclaw-module-host==0.1.0b0"));
        });
    }

    [Test]
    public async Task ScaffoldAsync_WhenRuntimeIsDotNet_UsesContractsPackageReference()
    {
        var sut = CreateSut();

        var result = await sut.ScaffoldAsync(new ModuleScaffoldService.ScaffoldSpec(
            ModuleId: "sample_dotnet",
            DisplayName: "Sample Dotnet",
            ToolPrefix: "sd"));

        var projectText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "SampleDotnet.csproj"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Files, Is.EqualTo(new[]
            {
                "SampleDotnet.csproj",
                "SampleDotnetModule.cs",
                "module.json"
            }));
            Assert.That(projectText, Does.Contain("<PackageReference Include=\"SharpClaw.Contracts\" />"));
            Assert.That(projectText, Does.Not.Contain("<HintPath>"));
        });
    }

    [Test]
    public async Task WriteFileAsync_AllowsScriptModuleFiles()
    {
        var lifecycle = new FakeLifecycleManager(_externalModulesDir);
        var workspace = new ModuleWorkspaceService(lifecycle);

        var moduleFile = await workspace.WriteFileAsync("sample_node", "module.mjs", "export {};");
        var typeScriptFile = await workspace.WriteFileAsync("sample_node", "src/index.ts", "export {};");
        var pythonFile = await workspace.WriteFileAsync("sample_python", "module.py", "host = None");
        var pyprojectFile = await workspace.WriteFileAsync("sample_python", "pyproject.toml", "[project]");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(moduleFile.Path), Is.True);
            Assert.That(File.Exists(typeScriptFile.Path), Is.True);
            Assert.That(File.Exists(pythonFile.Path), Is.True);
            Assert.That(File.Exists(pyprojectFile.Path), Is.True);
        });
    }

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

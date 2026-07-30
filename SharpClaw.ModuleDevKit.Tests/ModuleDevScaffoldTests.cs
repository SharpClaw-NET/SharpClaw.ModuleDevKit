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
        var manifestFile = await workspace.WriteFileAsync("sample_module", "module.json", "{}");
        var readmeFile = await workspace.WriteFileAsync("sample_module", "README.md", "Module notes");

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(sourceFile.Path), Is.True);
            Assert.That(File.Exists(projectFile.Path), Is.True);
            Assert.That(File.Exists(manifestFile.Path), Is.True);
            Assert.That(File.Exists(readmeFile.Path), Is.True);
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

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.ModuleDev;

namespace SharpClaw.ModuleDevKit.Tests;

[TestFixture]
public sealed class ModuleDevAgentWorkflowTests
{
    private string _externalModulesDir = null!;

    [SetUp]
    public void SetUp()
    {
        _externalModulesDir = Path.Combine(
            Path.GetTempPath(),
            "SharpClawModuleDevAgentWorkflowTests",
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
    public void GetToolDefinitions_ExposeAgentWorkflowTools()
    {
        var module = new ModuleDevModule();

        var names = module.GetToolDefinitions().Select(tool => tool.Name).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("scaffold_module"));
            Assert.That(names, Does.Contain("get_sdk_reference"));
            Assert.That(names, Does.Contain("apply_module_files"));
            Assert.That(names, Does.Contain("record_conversation_steering"));
            Assert.That(names, Does.Contain("list_conversation_steering"));
        });
    }

    [Test]
    public async Task GetSdkReference_ReturnsDotNetReferenceForAgents()
    {
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(new RecordingLifecycle(_externalModulesDir));
        using var parameters = JsonDocument.Parse("""{"topic":"dotnet"}""");

        var result = await module.ExecuteToolAsync(
            "get_sdk_reference",
            parameters.RootElement,
            Job(),
            provider,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("SharpClaw .NET module SDK"));
            Assert.That(result, Does.Contain("SharpClaw.Contracts"));
        });
    }

    [Test]
    public async Task ApplyModuleFiles_WritesLoadsAndSteersWorkflowResult()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering);
        var channelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var threadId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var parameters = JsonDocument.Parse($$$"""
            {
              "module_id": "sample_dotnet",
              "build": false,
              "load": true,
              "files": [
                {
                  "relative_path": "module.json",
                  "content": "{\"id\":\"sample_dotnet\",\"displayName\":\"Sample Dotnet\",\"version\":\"0.1.0-beta\",\"toolPrefix\":\"sd\",\"runtime\":\"dotnet\",\"entryAssembly\":\"SampleDotnet.dll\",\"minHostVersion\":\"0.1.0-beta\"}"
                },
                {
                  "relative_path": "SampleDotnet.csproj",
                  "content": "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                },
                {
                  "relative_path": "SampleDotnetModule.cs",
                  "content": "public sealed class SampleDotnetModule {}"
                }
              ],
              "conversation": {
                "channel_id": "{{{channelId}}}",
                "thread_id": "{{{threadId}}}"
              }
            }
            """);

        var result = await module.ExecuteToolAsync(
            "apply_module_files",
            parameters.RootElement,
            Job(channelId),
            provider,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(result);

        Assert.Multiple(() =>
        {
            Assert.That(payload.RootElement.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(payload.RootElement.GetProperty("runtime").GetString(), Is.EqualTo("dotnet"));
            Assert.That(payload.RootElement.TryGetProperty("verification", out _), Is.False);
            Assert.That(File.Exists(Path.Combine(_externalModulesDir, "sample_dotnet", "SampleDotnet.csproj")), Is.True);
            Assert.That(File.Exists(Path.Combine(_externalModulesDir, "sample_dotnet", "SampleDotnetModule.cs")), Is.True);
            Assert.That(lifecycle.LoadedDir, Is.EqualTo(Path.Combine(_externalModulesDir, "sample_dotnet")));
            Assert.That(steering.Requests, Has.Count.EqualTo(1));
            Assert.That(steering.Requests[0].ChannelId, Is.EqualTo(channelId));
            Assert.That(steering.Requests[0].ThreadId, Is.EqualTo(threadId));
            Assert.That(steering.Requests[0].Category, Is.EqualTo("module_workflow"));
            Assert.That(steering.Requests[0].Summary, Does.Contain("hot-loaded"));
        });
    }

    [Test]
    public async Task ApplyModuleFiles_WhenNonDotNetManifestIsNotFirst_DoesNotWriteBuildOrLoad()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering);
        var channelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        using var parameters = JsonDocument.Parse($$$"""
            {
              "module_id": "sample_node",
              "build": false,
              "load": false,
              "files": [
                {
                  "relative_path": "README.md",
                  "content": "This file must not be written."
                },
                {
                  "relative_path": "module.json",
                  "content": "{\"id\":\"sample_node\",\"displayName\":\"Sample Node\",\"version\":\"0.1.0-beta\",\"toolPrefix\":\"sn\",\"runtime\":\"node\",\"entryAssembly\":\"SampleNode.dll\",\"minHostVersion\":\"0.1.0-beta\"}"
                }
              ],
              "conversation": {
                "channel_id": "{{{channelId}}}"
              }
            }
            """);

        var result = await module.ExecuteToolAsync(
            "apply_module_files",
            parameters.RootElement,
            Job(channelId),
            provider,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(result);
        var moduleDir = Path.Combine(_externalModulesDir, "sample_node");

        Assert.Multiple(() =>
        {
            Assert.That(payload.RootElement.GetProperty("success").GetBoolean(), Is.False);
            Assert.That(payload.RootElement.GetProperty("error").GetString(), Does.Contain("only supports 'dotnet' modules"));
            Assert.That(payload.RootElement.TryGetProperty("build", out _), Is.False);
            Assert.That(Directory.Exists(moduleDir), Is.False);
            Assert.That(lifecycle.LoadCalls, Is.Zero);
            Assert.That(steering.Requests, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ApplyModuleFiles_WhenRuntimeRequestIsProvided_FailsBeforeWriting()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering);
        var channelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        using var parameters = JsonDocument.Parse($$$"""
            {
              "module_id": "sample_node",
              "runtime": "node",
              "build": false,
              "load": false,
              "files": [
                {
                  "relative_path": "README.md",
                  "content": "This file must not be written."
                }
              ],
              "conversation": {
                "channel_id": "{{{channelId}}}"
              }
            }
            """);

        var result = await module.ExecuteToolAsync(
            "apply_module_files",
            parameters.RootElement,
            Job(channelId),
            provider,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(result);

        Assert.Multiple(() =>
        {
            Assert.That(payload.RootElement.GetProperty("success").GetBoolean(), Is.False);
            Assert.That(payload.RootElement.GetProperty("error").GetString(), Does.Contain("does not accept a runtime request"));
            Assert.That(File.Exists(Path.Combine(_externalModulesDir, "sample_node", "README.md")), Is.False);
            Assert.That(lifecycle.LoadCalls, Is.Zero);
            Assert.That(steering.Requests, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void ScaffoldModule_WhenRuntimeRequestIsProvided_RejectsIt()
    {
        var module = new ModuleDevModule();
        using var parameters = JsonDocument.Parse("""
            {
              "module_id": "sample_node",
              "display_name": "Sample Node",
              "tool_prefix": "sn",
              "runtime": "node"
            }
            """);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await module.ExecuteToolAsync(
                "scaffold_module",
                parameters.RootElement,
                Job(),
                CreateProvider(new RecordingLifecycle(_externalModulesDir)),
                CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("does not accept a runtime request"));
            Assert.That(Directory.Exists(Path.Combine(_externalModulesDir, "sample_node")), Is.False);
        });
    }

    private static ServiceProvider CreateProvider(
        RecordingLifecycle lifecycle,
        RecordingConversationSteering? steering = null)
    {
        var services = new ServiceCollection();
        new ModuleDevModule().ConfigureServices(services);
        services.AddSingleton<IModuleLifecycleManager>(lifecycle);
        services.AddSingleton<IModuleInfoProvider>(new EmptyModuleInfoProvider());
        services.AddSingleton<IConversationSteering>(steering ?? new RecordingConversationSteering());
        return services.BuildServiceProvider();
    }

    private static AgentJobContext Job(Guid? channelId = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            channelId ?? Guid.Empty,
            ResourceId: null,
            ActionKey: "mdk_test");

    private sealed class RecordingLifecycle(string externalModulesDir) : IModuleLifecycleManager
    {
        public string ExternalModulesDir { get; } = externalModulesDir;
        public string? LoadedDir { get; private set; }
        public int LoadCalls { get; private set; }

        public bool IsModuleRegistered(string moduleId) => false;
        public bool IsToolPrefixRegistered(string toolPrefix) => false;
        public (ISharpClawCoreModule Module, string ToolName)? FindToolByName(string toolName) => null;

        public Task<ModuleStateResponse> LoadExternalAsync(
            string moduleDir,
            IServiceProvider hostServices,
            CancellationToken ct = default)
        {
            LoadCalls++;
            LoadedDir = moduleDir;
            return Task.FromResult(State(Path.GetFileName(moduleDir)));
        }

        public Task UnloadExternalAsync(string moduleId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ModuleStateResponse> ReloadExternalAsync(
            string moduleId,
            IServiceProvider hostServices,
            CancellationToken ct = default) =>
            Task.FromResult(State(moduleId));

        private static ModuleStateResponse State(string moduleId) =>
            new(
                moduleId,
                "Loaded Module",
                "lm",
                true,
                "0.1.0-beta",
                true,
                true,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
    }

    private sealed class EmptyModuleInfoProvider : IModuleInfoProvider
    {
        public IReadOnlyList<ModuleInfo> GetAllModules() => [];
    }

    private sealed class RecordingConversationSteering : IConversationSteering
    {
        public List<ConversationSteeringRequest> Requests { get; } = [];

        public Task<ConversationSteeringResponse> AddAsync(
            ConversationSteeringRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ConversationSteeringResponse(
                Guid.NewGuid(),
                request.ChannelId,
                request.ThreadId,
                request.Summary,
                DateTimeOffset.UnixEpoch,
                request.Source,
                request.Category));
        }

        public Task<IReadOnlyList<ConversationSteeringResponse>> ListAsync(
            Guid channelId,
            Guid? threadId = null,
            int limit = 20,
            CancellationToken ct = default)
        {
            IReadOnlyList<ConversationSteeringResponse> rows = Requests
                .Where(request => request.ChannelId == channelId && request.ThreadId == threadId)
                .Take(limit)
                .Select(request => new ConversationSteeringResponse(
                    Guid.NewGuid(),
                    request.ChannelId,
                    request.ThreadId,
                    request.Summary,
                    DateTimeOffset.UnixEpoch,
                    request.Source,
                    request.Category))
                .ToList();
            return Task.FromResult(rows);
        }
    }
}

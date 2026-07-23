using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.ModuleDev;
using SharpClaw.Modules.ModuleDev.Services;

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
            Assert.That(names, Does.Contain("get_sdk_reference"));
            Assert.That(names, Does.Contain("apply_module_files"));
            Assert.That(names, Does.Contain("record_conversation_steering"));
            Assert.That(names, Does.Contain("list_conversation_steering"));
        });
    }

    [Test]
    public async Task GetSdkReference_ReturnsRuntimeReferenceForAgents()
    {
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(new RecordingLifecycle(_externalModulesDir));
        using var parameters = JsonDocument.Parse("""{"topic":"javascript"}""");

        var result = await module.ExecuteToolAsync(
            "get_sdk_reference",
            parameters.RootElement,
            Job(),
            provider,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("@sharpclaw/module-host"));
            Assert.That(result, Does.Contain("addConversationSteering"));
        });
    }

    [Test]
    public async Task ApplyModuleFiles_WritesLoadsAndSteersWorkflowResult()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var commands = new RecordingCommandRunner();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering: steering, commandRunner: commands);
        var channelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var threadId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var parameters = JsonDocument.Parse($$$"""
            {
              "module_id": "sample_node",
              "runtime": "node",
              "load": true,
              "files": [
                {
                  "relative_path": "module.json",
                  "content": "{\"id\":\"sample_node\",\"displayName\":\"Sample Node\",\"toolPrefix\":\"sn\",\"runtime\":\"node\",\"entrypoint\":\"module.mjs\",\"entryAssembly\":\"\"}"
                },
                {
                  "relative_path": "module.mjs",
                  "content": "export {};"
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
        var stepNames = payload.RootElement.GetProperty("verification")
            .GetProperty("steps")
            .EnumerateArray()
            .Select(step => step.GetProperty("name").GetString())
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(payload.RootElement.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(stepNames, Is.EqualTo(new[] { "node_syntax" }));
            Assert.That(File.Exists(Path.Combine(_externalModulesDir, "sample_node", "module.mjs")), Is.True);
            Assert.That(lifecycle.LoadedDir, Is.EqualTo(Path.Combine(_externalModulesDir, "sample_node")));
            Assert.That(commands.Commands, Has.Count.EqualTo(1));
            Assert.That(commands.Commands[0].FileName, Is.EqualTo("node"));
            Assert.That(commands.Commands[0].Arguments, Is.EqualTo(new[] { "--check", "module.mjs" }));
            Assert.That(steering.Requests, Has.Count.EqualTo(1));
            Assert.That(steering.Requests[0].ChannelId, Is.EqualTo(channelId));
            Assert.That(steering.Requests[0].ThreadId, Is.EqualTo(threadId));
            Assert.That(steering.Requests[0].Category, Is.EqualTo("module_workflow"));
            Assert.That(steering.Requests[0].Summary, Does.Contain("verified"));
            Assert.That(steering.Requests[0].Summary, Does.Contain("hot-loaded"));
        });
    }

    [Test]
    public async Task ApplyModuleFiles_WhenNodeVerificationFails_DoesNotLoadAndSteersDiagnostics()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var commands = new RecordingCommandRunner();
        commands.Enqueue(exitCode: 1, stderr: "SyntaxError: Unexpected token");
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering: steering, commandRunner: commands);
        var channelId = Guid.Parse("abababab-abab-abab-abab-abababababab");
        using var parameters = JsonDocument.Parse($$$"""
            {
              "module_id": "bad_node",
              "runtime": "node",
              "files": [
                {
                  "relative_path": "module.json",
                  "content": "{\"id\":\"bad_node\",\"displayName\":\"Bad Node\",\"toolPrefix\":\"bn\",\"runtime\":\"node\",\"entrypoint\":\"module.mjs\",\"entryAssembly\":\"\"}"
                },
                {
                  "relative_path": "module.mjs",
                  "content": "export const broken = ;"
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
            Assert.That(
                payload.RootElement.GetProperty("verification").GetProperty("success").GetBoolean(),
                Is.False);
            Assert.That(lifecycle.LoadedDir, Is.Null);
            Assert.That(commands.Commands, Has.Count.EqualTo(1));
            Assert.That(steering.Requests, Has.Count.EqualTo(1));
            Assert.That(steering.Requests[0].Category, Is.EqualTo("module_verify"));
            Assert.That(steering.Requests[0].Details, Does.Contain("Unexpected token"));
        });
    }

    [Test]
    public async Task ApplyModuleFiles_NodeDependencyInstallAndDeclaredVerifyRunBeforeLoad()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var commands = new RecordingCommandRunner();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering: steering, commandRunner: commands);
        var channelId = Guid.Parse("cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd");
        using var parameters = JsonDocument.Parse($$$"""
            {
              "module_id": "verified_node",
              "runtime": "node",
              "install_dependencies": true,
              "run_declared_verify": true,
              "load": false,
              "files": [
                {
                  "relative_path": "module.json",
                  "content": "{\"id\":\"verified_node\",\"displayName\":\"Verified Node\",\"toolPrefix\":\"vn\",\"runtime\":\"node\",\"entrypoint\":\"module.mjs\",\"entryAssembly\":\"\"}"
                },
                {
                  "relative_path": "module.mjs",
                  "content": "export {};"
                },
                {
                  "relative_path": "package.json",
                  "content": "{\"type\":\"module\",\"scripts\":{\"sharpclawVerify\":\"node --check module.mjs\"}}"
                },
                {
                  "relative_path": "package-lock.json",
                  "content": "{\"lockfileVersion\":3}"
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
        var commandLines = commands.Commands
            .Select(command => command.FileName + " " + string.Join(' ', command.Arguments))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(payload.RootElement.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(lifecycle.LoadedDir, Is.Null);
            Assert.That(commandLines, Is.EqualTo(new[]
            {
                "npm ci",
                "node --check module.mjs",
                "npm run sharpclawVerify"
            }));
            Assert.That(
                payload.RootElement.GetProperty("verification").GetProperty("steps").GetArrayLength(),
                Is.EqualTo(3));
        });
    }

    [Test]
    public async Task ApplyModuleFiles_PythonCompileAndDeclaredVerifyRunBeforeLoad()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var commands = new RecordingCommandRunner();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering: steering, commandRunner: commands);
        var channelId = Guid.Parse("efefefef-efef-efef-efef-efefefefefef");
        using var parameters = JsonDocument.Parse($$"""
            {
              "module_id": "verified_python",
              "runtime": "python",
              "load": true,
              "files": [
                {
                  "relative_path": "module.json",
                  "content": "{\"id\":\"verified_python\",\"displayName\":\"Verified Python\",\"toolPrefix\":\"vp\",\"runtime\":\"python\",\"entrypoint\":\"module.py\",\"entryAssembly\":\"\"}"
                },
                {
                  "relative_path": "module.py",
                  "content": "print('ok')\n"
                },
                {
                  "relative_path": "pyproject.toml",
                  "content": "[tool.sharpclaw]\nverify-command = \"python -m pytest\"\n"
                }
              ],
              "conversation": {
                "channel_id": "{{channelId}}"
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
        var commandLines = commands.Commands
            .Select(command => command.FileName + " " + string.Join(' ', command.Arguments))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(payload.RootElement.GetProperty("success").GetBoolean(), Is.True);
            Assert.That(lifecycle.LoadedDir, Is.EqualTo(Path.Combine(_externalModulesDir, "verified_python")));
            Assert.That(commandLines, Is.EqualTo(new[]
            {
                "python -m py_compile module.py",
                "python -m pytest"
            }));
            Assert.That(steering.Requests, Has.Count.EqualTo(1));
            Assert.That(steering.Requests[0].Summary, Does.Contain("verified"));
        });
    }

    [Test]
    public async Task ApplyModuleFiles_InvalidPythonVerifyCommandFailsAsVerificationNotWorkflowCrash()
    {
        var lifecycle = new RecordingLifecycle(_externalModulesDir);
        var steering = new RecordingConversationSteering();
        var commands = new RecordingCommandRunner();
        var module = new ModuleDevModule();
        await using var provider = CreateProvider(lifecycle, steering: steering, commandRunner: commands);
        var channelId = Guid.Parse("12121212-3434-5656-7878-909090909090");
        using var parameters = JsonDocument.Parse($$"""
            {
              "module_id": "unsafe_python",
              "runtime": "python",
              "files": [
                {
                  "relative_path": "module.json",
                  "content": "{\"id\":\"unsafe_python\",\"displayName\":\"Unsafe Python\",\"toolPrefix\":\"up\",\"runtime\":\"python\",\"entrypoint\":\"module.py\",\"entryAssembly\":\"\"}"
                },
                {
                  "relative_path": "module.py",
                  "content": "print('ok')\n"
                },
                {
                  "relative_path": "pyproject.toml",
                  "content": "[tool.sharpclaw]\nverify-command = \"python -m pytest && echo unsafe\"\n"
                }
              ],
              "conversation": {
                "channel_id": "{{channelId}}"
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
        var commandLines = commands.Commands
            .Select(command => command.FileName + " " + string.Join(' ', command.Arguments))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(payload.RootElement.GetProperty("success").GetBoolean(), Is.False);
            Assert.That(
                payload.RootElement.GetProperty("verification").GetProperty("error").GetString(),
                Does.Contain("shell metacharacters"));
            Assert.That(lifecycle.LoadedDir, Is.Null);
            Assert.That(commandLines, Is.EqualTo(new[] { "python -m py_compile module.py" }));
            Assert.That(steering.Requests, Has.Count.EqualTo(1));
            Assert.That(steering.Requests[0].Category, Is.EqualTo("module_verify"));
        });
    }

    private static ServiceProvider CreateProvider(
        RecordingLifecycle lifecycle,
        RecordingConversationSteering? steering = null,
        RecordingCommandRunner? commandRunner = null)
    {
        var services = new ServiceCollection();
        new ModuleDevModule().ConfigureServices(services);
        services.AddSingleton<IModuleLifecycleManager>(lifecycle);
        services.AddSingleton<IModuleRuntimeCommandRunner>(commandRunner ?? new RecordingCommandRunner());
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
        public string? ReloadedId { get; private set; }

        public bool IsModuleRegistered(string moduleId) => false;
        public bool IsToolPrefixRegistered(string toolPrefix) => false;
        public (ISharpClawCoreModule Module, string ToolName)? FindToolByName(string toolName) => null;

        public Task<ModuleStateResponse> LoadExternalAsync(
            string moduleDir,
            IServiceProvider hostServices,
            CancellationToken ct = default)
        {
            LoadedDir = moduleDir;
            return Task.FromResult(State(Path.GetFileName(moduleDir)));
        }

        public Task UnloadExternalAsync(string moduleId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ModuleStateResponse> ReloadExternalAsync(
            string moduleId,
            IServiceProvider hostServices,
            CancellationToken ct = default)
        {
            ReloadedId = moduleId;
            return Task.FromResult(State(moduleId));
        }

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

    private sealed class RecordingCommandRunner : IModuleRuntimeCommandRunner
    {
        private readonly Queue<(int ExitCode, string Stdout, string Stderr, bool TimedOut)> _outcomes = new();

        public List<ModuleRuntimeCommand> Commands { get; } = [];

        public void Enqueue(
            int exitCode = 0,
            string stdout = "",
            string stderr = "",
            bool timedOut = false) =>
            _outcomes.Enqueue((exitCode, stdout, stderr, timedOut));

        public Task<ModuleRuntimeCommandResult> RunAsync(
            ModuleRuntimeCommand command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            var outcome = _outcomes.Count > 0
                ? _outcomes.Dequeue()
                : (ExitCode: 0, Stdout: "", Stderr: "", TimedOut: false);
            return Task.FromResult(new ModuleRuntimeCommandResult(
                command.FileName,
                command.Arguments,
                command.WorkingDirectory,
                outcome.ExitCode,
                outcome.TimedOut,
                outcome.Stdout,
                outcome.Stderr,
                TimeSpan.FromMilliseconds(2)));
        }
    }
}

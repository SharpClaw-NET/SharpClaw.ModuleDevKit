using System.Text;
using System.Text.Json;
using NUnit.Framework;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;
using SharpClaw.ModuleSDK.HostOperations;
using SharpClaw.Modules.AgentOrchestration.Contracts;
using SharpClaw.Modules.ModuleDev;
using SharpClaw.Modules.ModuleDev.Handlers;
using SharpClaw.Modules.ModuleDev.Services;

namespace SharpClaw.ModuleDevKit.Tests;

[TestFixture]
public sealed class ModuleDevBoundaryTests
{
    private string _externalModulesDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _externalModulesDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "module-dev-boundary",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_externalModulesDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_externalModulesDirectory))
            Directory.Delete(_externalModulesDirectory, recursive: true);
    }

    [Test]
    public void Compile_ProducesCompleteNeutralOutOfProcessGraph()
    {
        var graph = SharpClawModuleCompiler.Compile(
            new ModuleDevModule(),
            LoadManifest(),
            new ModuleCompilationOptions { HostingMode = ModuleHostingMode.OutOfProcess });

        Assert.Multiple(() =>
        {
            Assert.That(graph.Identity.Id, Is.EqualTo(ModuleDevContracts.ModuleId));
            Assert.That(graph.Actions.Select(action => action.Descriptor.Key.Value), Is.EqualTo(new[]
            {
                "module-dev.read",
                "module-dev.mutate",
            }));
            Assert.That(graph.ActionEntries, Has.Count.EqualTo(2));
            Assert.That(graph.Tools, Has.Count.EqualTo(17));
            Assert.That(graph.Application.CliCommands, Has.Count.EqualTo(1));
            Assert.That(graph.Application.Endpoints, Has.Count.EqualTo(11));
            Assert.That(graph.Storage, Is.Empty);
            Assert.That(graph.Contracts, Is.Empty);
        });
    }

    [Test]
    public void Descriptors_SeparateReadAndIrreversibleMutationAuthority()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ModuleDevContracts.ReadDescriptor.HasIrreversibleEffects, Is.False);
            Assert.That(
                ModuleDevContracts.ReadDescriptor.RepeatPolicy.Kind,
                Is.EqualTo(ActionRepeatKind.Idempotent));
            Assert.That(ModuleDevContracts.MutationDescriptor.HasIrreversibleEffects, Is.True);
            Assert.That(
                ModuleDevContracts.MutationDescriptor.RepeatPolicy.Kind,
                Is.EqualTo(ActionRepeatKind.None));
            Assert.That(
                ModuleDevContracts.MutationDescriptor.Capabilities.HasFlag(
                    ActionInterceptionCapabilities.Repeat),
                Is.False);
            Assert.That(ModuleDevContracts.ReadTerminalId, Is.Not.EqualTo(ModuleDevContracts.MutationTerminalId));
        });
    }

    [Test]
    public void ToolInventory_PreservesEveryLegacyToolAsOneNormalTool()
    {
        Assert.That(
            ModuleDevContracts.ToolDescriptors.Select(tool => tool.Name),
            Is.EqualTo(new[]
            {
                "scaffold_module",
                "write_file",
                "read_file",
                "list_files",
                "build_module",
                "load_module",
                "unload_module",
                "test_tool",
                "inspect_process",
                "discover_com_interfaces",
                "enumerate_dev_environment",
                "get_sdk_reference",
                "apply_module_files",
                "record_conversation_steering",
                "list_conversation_steering",
                "describe_module_system",
                "list_loaded_modules",
            }));
    }

    [Test]
    public async Task Lifecycle_IsCancellationSafeAndHasNoHiddenState()
    {
        var module = new ModuleDevModule();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await module.StartAsync(
            new ModuleStartContext(module.Identity, "test", "contract", new ExtensionFeatureSet([])),
            cancellation.Token);
        await module.StopAsync(cancellation.Token);

        Assert.Pass();
    }

    [Test]
    public void ReadTerminal_RejectsMutationBeforeHostOrFileAccess()
    {
        var fixture = CreateFixture();
        var context = fixture.Host.CreateActionContext(new ModuleDevAction(
            ModuleDevOperation.WriteFile,
            JsonSerializer.SerializeToElement(new
            {
                module_id = "sample_module",
                relative_path = "Sample.cs",
                content = "sealed class Sample {}",
            })));

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await fixture.ReadTerminal.InvokeAsync(context, CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Host.CrossSidecarKeys, Is.Empty);
            Assert.That(Directory.GetFiles(_externalModulesDirectory, "*", SearchOption.AllDirectories), Is.Empty);
        });
    }

    [Test]
    public void UnauthorizedToolRequest_PerformsNoHostCallAndNoWrite()
    {
        var fixture = CreateFixture(rejectRoot: true);
        var invocation = ToolInvocation("write_file", new
        {
            module_id = "sample_module",
            relative_path = "Sample.cs",
            content = "sealed class Sample {}",
        });

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Tool.InvokeAsync(invocation, CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Host.RootCalls, Is.EqualTo(1));
            Assert.That(fixture.Host.CrossSidecarKeys, Is.Empty);
            Assert.That(Directory.GetFiles(_externalModulesDirectory, "*", SearchOption.AllDirectories), Is.Empty);
        });
    }

    [Test]
    public async Task AuthorizedWrite_RunsOnceThroughHostListAndTypedMutation()
    {
        var fixture = CreateFixture();
        var result = await fixture.Tool.InvokeAsync(
            ToolInvocation("write_file", new
            {
                module_id = "sample_module",
                relative_path = "Sample.cs",
                content = "sealed class Sample {}",
            }),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False);
            Assert.That(fixture.Host.RootCalls, Is.EqualTo(1));
            Assert.That(
                fixture.Host.CrossSidecarKeys,
                Is.EqualTo(new[] { HostOperationActionDescriptors.ModuleList.Key.Value }));
            Assert.That(
                File.ReadAllText(Path.Combine(_externalModulesDirectory, "sample_module", "Sample.cs")),
                Is.EqualTo("sealed class Sample {}"));
        });
    }

    [Test]
    public async Task LifecycleAndToolOperations_UseOnlyNeutralHostActionsOnce()
    {
        var fixture = CreateFixture();
        await fixture.Tool.InvokeAsync(
            ToolInvocation("load_module", new { module_id = "sample_module" }),
            CancellationToken.None);
        await fixture.Tool.InvokeAsync(
            ToolInvocation("test_tool", new
            {
                tool_name = "sample.echo",
                parameters = new { text = "hello" },
            }),
            CancellationToken.None);

        Assert.That(
            fixture.Host.CrossSidecarKeys,
            Is.EqualTo(new[]
            {
                HostOperationActionDescriptors.ModuleLifecycle.Key.Value,
                HostOperationActionDescriptors.ToolInvoke.Key.Value,
            }));
    }

    [Test]
    public async Task LoadedModules_PreserveExportedContractNames()
    {
        var fixture = CreateFixture();
        var result = await fixture.Tool.InvokeAsync(
            ToolInvocation("list_loaded_modules", new { }),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Does.Contain("module-dev.contract"));
            Assert.That(
                fixture.Host.CrossSidecarKeys,
                Is.EqualTo(new[] { HostOperationActionDescriptors.ModuleList.Key.Value }));
        });
    }

    [Test]
    public async Task SteeringOperations_UseContextTypedEntries()
    {
        var fixture = CreateFixture();
        var channelId = Guid.NewGuid();
        await fixture.Tool.InvokeAsync(
            ToolInvocation("record_conversation_steering", new
            {
                channel_id = channelId.ToString("D"),
                summary = "Build completed.",
            }),
            CancellationToken.None);
        await fixture.Tool.InvokeAsync(
            ToolInvocation("list_conversation_steering", new
            {
                channel_id = channelId.ToString("D"),
                limit = 10,
            }),
            CancellationToken.None);

        Assert.That(
            fixture.Host.CrossSidecarKeys,
            Is.EqualTo(new[]
            {
                ContextSteeringActionDescriptors.Record.Key.Value,
                ContextSteeringActionDescriptors.List.Key.Value,
            }));
    }

    [Test]
    public async Task CliMutation_UsesTheSameActionAndLifecyclePath()
    {
        var fixture = CreateFixture();
        var invocationId = Guid.NewGuid();
        var result = await fixture.Cli.ExecuteAsync(
            new ModuleCliInvocation(
                invocationId,
                "mdk",
                ["load", "sample_module"],
                TestHost.CreateHostContext(HostActionEntryIngress.Cli, "mdk", invocationId)),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(
                fixture.Host.CrossSidecarKeys,
                Is.EqualTo(new[] { HostOperationActionDescriptors.ModuleLifecycle.Key.Value }));
        });
    }

    [Test]
    public async Task HttpFileWrite_UsesTheSameActionAndWorkspacePath()
    {
        var fixture = CreateFixture();
        var route = ModuleDevEndpointHandler.Routes.Single(item => item.Id == "module-dev.files.write");
        var invocationId = Guid.NewGuid();
        var request = new HostEndpointRouteRequest(
            new HostEndpointInvocation(
                invocationId,
                route.Id,
                TestHost.CreateHostContext(HostActionEntryIngress.Endpoint, route.Id, invocationId)),
            route.ToRouteIdentity(),
            new Dictionary<string, string[]>(),
            new Dictionary<string, string[]>(),
            JsonSerializer.SerializeToUtf8Bytes(new { content = "sealed class Sample {}" }))
        {
            RouteValues = new Dictionary<string, string[]>
            {
                ["moduleId"] = ["sample_module"],
                ["path"] = ["Sample.cs"],
            },
        };

        var response = await fixture.Endpoint.InvokeAsync(
            request,
            fixture.Host,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(200));
            Assert.That(
                File.Exists(Path.Combine(_externalModulesDirectory, "sample_module", "Sample.cs")),
                Is.True);
        });
    }

    [Test]
    public void CancelledRequest_StopsBeforeActionIssuanceAndEffects()
    {
        var fixture = CreateFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await fixture.Tool.InvokeAsync(
                ToolInvocation("write_file", new
                {
                    module_id = "sample_module",
                    relative_path = "Sample.cs",
                    content = "sealed class Sample {}",
                }),
                cancellation.Token));
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Host.RootCalls, Is.Zero);
            Assert.That(Directory.GetFiles(_externalModulesDirectory, "*", SearchOption.AllDirectories), Is.Empty);
        });
    }

    [Test]
    public async Task Scaffold_GeneratesCurrentNeutralModuleSourceAndManifest()
    {
        var fixture = CreateFixture();
        await fixture.Tool.InvokeAsync(
            ToolInvocation("scaffold_module", new
            {
                module_id = "sample_module",
                display_name = "Sample Module",
                tool_prefix = "sm",
                tools = new[] { new { name = "echo", description = "Return text." } },
            }),
            CancellationToken.None);

        var directory = Path.Combine(_externalModulesDirectory, "sample_module");
        var source = await File.ReadAllTextAsync(Path.Combine(directory, "SampleModuleModule.cs"));
        var project = await File.ReadAllTextAsync(Path.Combine(directory, "SampleModule.csproj"));
        var manifest = await File.ReadAllTextAsync(Path.Combine(directory, "module.json"));
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("ISharpClawModule"));
            Assert.That(source, Does.Contain("IToolHandler"));
            Assert.That(source, Does.Not.Contain("ISharpClawCoreModule"));
            Assert.That(project, Does.Contain("SharpClaw.ModuleSDK"));
            Assert.That(project, Does.Contain("[0.5.0-beta.33]"));
            Assert.That(manifest, Does.Contain("\"hostMode\": \"sidecar\""));
            Assert.That(manifest, Does.Contain("\"moduleType\": \"SampleModule.SampleModuleModule\""));
        });
    }

    [Test]
    public async Task Workflow_UsesWorkspaceLifecycleToolAndContextBoundaries()
    {
        var fixture = CreateFixture();
        var channelId = Guid.NewGuid();
        var result = await fixture.Tool.InvokeAsync(
            ToolInvocation("apply_module_files", new
            {
                module_id = "sample_module",
                build = false,
                load = true,
                files = new[]
                {
                    new { relative_path = "Sample.cs", content = "sealed class Sample {}" },
                },
                test_tools = new[]
                {
                    new { tool_name = "sample.echo", parameters = new { text = "hello" } },
                },
                conversation = new { channel_id = channelId.ToString("D") },
            }),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Does.Contain("\"success\": true"));
            Assert.That(
                fixture.Host.CrossSidecarKeys,
                Is.EqualTo(new[]
                {
                    HostOperationActionDescriptors.ModuleList.Key.Value,
                    HostOperationActionDescriptors.ModuleLifecycle.Key.Value,
                    HostOperationActionDescriptors.ToolInvoke.Key.Value,
                    ContextSteeringActionDescriptors.Record.Key.Value,
                }));
            Assert.That(
                File.Exists(Path.Combine(_externalModulesDirectory, "sample_module", "Sample.cs")),
                Is.True);
        });
    }

    [Test]
    public void WorkspaceRootChange_FailsClosed()
    {
        var fixture = CreateFixture();
        var first = ToolInvocation("list_files", new { module_id = "sample_module" });
        fixture.Tool.InvokeAsync(first, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        fixture.Host.ExternalModulesDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "different-root",
            Guid.NewGuid().ToString("N"));

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Tool.InvokeAsync(first, CancellationToken.None));
    }

    private Fixture CreateFixture(bool rejectRoot = false)
    {
        var host = new TestHost(_externalModulesDirectory) { RejectRoot = rejectRoot };
        var workspace = new ModuleWorkspaceService();
        var operations = new ModuleDevOperations(
            workspace,
            new ModuleBuildService(workspace),
            new ModuleScaffoldService(workspace),
            new SharpClawSdkReferenceService(),
            new DevEnvironmentService(),
            new ProcessInspectionService(),
            new ComTypeLibInspector());
        var readTerminal = new ModuleDevReadTerminal(operations);
        var mutationTerminal = new ModuleDevMutationTerminal(operations);
        var gateway = new ModuleDevActionGateway(readTerminal, mutationTerminal);
        return new Fixture(
            host,
            readTerminal,
            new ModuleDevToolHandler(host, gateway),
            new ModuleDevCliHandler(host, gateway),
            new ModuleDevEndpointHandler(gateway));
    }

    private static ToolInvocation ToolInvocation(string toolName, object arguments)
    {
        var invocationId = Guid.NewGuid();
        return new ToolInvocation(
            invocationId,
            null,
            $"call-{invocationId:N}",
            toolName,
            JsonSerializer.SerializeToElement(arguments),
            TestHost.CreateHostContext(HostActionEntryIngress.Tool, toolName, invocationId));
    }

    private static ModuleManifest LoadManifest()
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "modules",
            ModuleDevContracts.ModuleId,
            "module.json");
        return JsonSerializer.Deserialize<ModuleManifest>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The module manifest could not be loaded.");
    }

    private sealed record Fixture(
        TestHost Host,
        ModuleDevReadTerminal ReadTerminal,
        ModuleDevToolHandler Tool,
        ModuleDevCliHandler Cli,
        ModuleDevEndpointHandler Endpoint);

    private sealed class TestHost(string externalModulesDirectory)
        : IHostActionEntry, IModuleCrossSidecarActionEntry
    {
        public string ExternalModulesDirectory { get; set; } = externalModulesDirectory;
        public bool RejectRoot { get; set; }
        public int RootCalls { get; private set; }
        public List<string> CrossSidecarKeys { get; } = [];

        public async ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
            HostActionEntryRequest<TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RootCalls++;
            if (RejectRoot)
            {
                return new TestOutcome<TResult>(
                    ActionOutcomeKind.Failed,
                    default,
                    new ExecutionError("test_rejected", "Rejected."));
            }

            var context = new ActionContext<TAction>(
                request.Context.InvocationId,
                request.Context.ParentInvocationId,
                request.Context.TraceId,
                request.Context.IdempotencyKey,
                request.Context.Depth,
                request.Context.Attempt,
                request.Context.Deadline,
                request.Descriptor.Key,
                ModuleDevContracts.ModuleId,
                request.Context.Caller,
                request.Action,
                request.Context.Features,
                new ActionPipelineSnapshot("test", []))
            {
                HostActionEntry = this,
            };
            var result = await terminal.InvokeAsync(context, cancellationToken);
            return new TestOutcome<TResult>(ActionOutcomeKind.Completed, result, null);
        }

        public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
            HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
            IHostActionEntryTerminal<TAction, TResult> terminal,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IActionOutcome<TResult>> InvokeCrossSidecarAsync<TAction, TResult>(
            ModuleCrossSidecarActionEntryRequest<TAction, TResult> request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CrossSidecarKeys.Add(request.Descriptor.Key.Value);
            object result = request.Descriptor.Key.Value switch
            {
                "host.module.list" => new HostModuleListResult(
                    ExternalModulesDirectory,
                    [new HostModuleSummary(
                        State(ModuleDevContracts.ModuleId, "mdk"),
                        ["module-dev.contract"])]),
                "host.module.lifecycle" => Lifecycle((HostModuleLifecycleAction)(object)request.Action!),
                "host.tool.invoke" => ToolInvocationOutcome.Completed(ToolResult.Text("tool-result")),
                "context.steering.record" => Steering((ContextRecordSteeringAction)(object)request.Action!),
                "context.steering.list" => Array.Empty<ContextSteeringRecord>(),
                _ => throw new NotSupportedException(request.Descriptor.Key.Value),
            };
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                new TestOutcome<TResult>(
                    ActionOutcomeKind.Completed,
                    (TResult)result,
                    null));
        }

        public ActionContext<ModuleDevAction> CreateActionContext(ModuleDevAction action) =>
            new(
                Guid.NewGuid(),
                null,
                Guid.NewGuid(),
                Guid.NewGuid(),
                0,
                1,
                DateTimeOffset.UtcNow.AddMinutes(1),
                ModuleDevContracts.ReadDescriptor.Key,
                ModuleDevContracts.ModuleId,
                Principal,
                action,
                new ExtensionFeatureSet([]),
                new ActionPipelineSnapshot("test", []))
            {
                HostActionEntry = this,
            };

        public static HostActionEntryRequestContext CreateHostContext(
            HostActionEntryIngress ingress,
            string primaryIdentity,
            Guid invocationId)
        {
            var key = new SharpClawActionKey("test.ingress");
            return new HostActionEntryRequestContext(
                Guid.NewGuid(),
                "test-capability",
                ingress,
                invocationId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Principal,
                new ExtensionFeatureSet([]),
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMinutes(1),
                DateTimeOffset.UtcNow.AddMinutes(2))
            {
                Contribution = new HostActionEntryContribution(
                    new HostActionEntryIngressBinding(ingress, primaryIdentity),
                    new HostActionEntryLineage(
                        key,
                        1,
                        "descriptor",
                        "input",
                        1,
                        "schema",
                        null,
                        null)),
            };
        }

        private static RequestPrincipal Principal { get; } =
            new("module-dev-test", "ModuleDev Test", new HashSet<string> { "administrator" });

        private static HostModuleLifecycleResult Lifecycle(HostModuleLifecycleAction action) =>
            new(
                action.Operation,
                action.ModuleId,
                action.Operation == HostModuleLifecycleOperation.Unload
                    ? null
                    : State(action.ModuleId, "ext"));

        private static ModuleStateResponse State(string moduleId, string prefix) =>
            new(
                moduleId,
                moduleId,
                prefix,
                true,
                "0.1.0-beta",
                true,
                true,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);

        private static ContextSteeringRecord Steering(ContextRecordSteeringAction action) =>
            new(
                Guid.NewGuid(),
                action.ChannelId,
                action.ThreadId,
                action.Source,
                action.Category,
                action.Summary,
                action.Details,
                action.ClientType,
                Principal,
                DateTimeOffset.UnixEpoch);
    }

    private sealed record TestOutcome<TResult>(
        ActionOutcomeKind Kind,
        TResult? Result,
        ExecutionError? Error) : IActionOutcome<TResult>
    {
        public ContinuationToken? Continuation => null;
        public ActionUncertainty? Uncertainty => null;
    }
}

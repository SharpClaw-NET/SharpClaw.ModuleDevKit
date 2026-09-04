using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;
using SharpClaw.ModuleSDK.HostOperations;
using SharpClaw.Modules.AgentOrchestration.Contracts;

namespace SharpClaw.Modules.ModuleDev.Services;

/// <summary>Executes ModuleDev effects after typed host authorization.</summary>
internal sealed class ModuleDevOperations(
    ModuleWorkspaceService workspace,
    ModuleBuildService build,
    ModuleScaffoldService scaffold,
    SharpClawSdkReferenceService sdkReference,
    DevEnvironmentService environment,
    ProcessInspectionService processInspection,
    ComTypeLibInspector comInspection)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public async ValueTask<ModuleDevActionResult> ExecuteAsync(
        ActionContext<ModuleDevAction> context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var hostActionEntry = context.HostActionEntry
            ?? throw new InvalidOperationException("The host action entry is unavailable.");
        var action = context.Action;
        var content = action.Operation switch
        {
            ModuleDevOperation.ScaffoldModule =>
                await ScaffoldModuleAsync(action.Parameters, hostActionEntry, ct),
            ModuleDevOperation.WriteFile =>
                await WriteFileAsync(action.Parameters, hostActionEntry, ct),
            ModuleDevOperation.ReadFile =>
                await ReadFileAsync(action.Parameters, hostActionEntry, ct),
            ModuleDevOperation.ListFiles =>
                await ListFilesAsync(action.Parameters, hostActionEntry, ct),
            ModuleDevOperation.BuildModule =>
                await BuildModuleAsync(action.Parameters, hostActionEntry, ct),
            ModuleDevOperation.LoadModule =>
                await ChangeModuleStateAsync(
                    action.Parameters,
                    HostModuleLifecycleOperation.Load,
                    hostActionEntry,
                    ct),
            ModuleDevOperation.UnloadModule =>
                await ChangeModuleStateAsync(
                    action.Parameters,
                    HostModuleLifecycleOperation.Unload,
                    hostActionEntry,
                    ct),
            ModuleDevOperation.ReloadModule =>
                await ChangeModuleStateAsync(
                    action.Parameters,
                    HostModuleLifecycleOperation.Reload,
                    hostActionEntry,
                    ct),
            ModuleDevOperation.TestTool =>
                await TestToolAsync(action.Parameters, action.ConversationId, hostActionEntry, ct),
            ModuleDevOperation.InspectProcess =>
                await InspectProcessAsync(action.Parameters, ct),
            ModuleDevOperation.DiscoverComInterfaces =>
                await DiscoverComInterfacesAsync(action.Parameters, ct),
            ModuleDevOperation.EnumerateDevEnvironment =>
                await EnumerateEnvironmentAsync(hostActionEntry, ct),
            ModuleDevOperation.GetSdkReference =>
                GetSdkReference(action.Parameters),
            ModuleDevOperation.ApplyModuleFiles =>
                await ApplyModuleFilesAsync(
                    action.Parameters,
                    action.ConversationId,
                    hostActionEntry,
                    ct),
            ModuleDevOperation.RecordConversationSteering =>
                await RecordSteeringAsync(action.Parameters, hostActionEntry, ct),
            ModuleDevOperation.ListConversationSteering =>
                await ListSteeringAsync(action.Parameters, hostActionEntry, ct),
            ModuleDevOperation.DescribeModuleSystem =>
                DescribeModuleSystem(),
            ModuleDevOperation.ListLoadedModules =>
                await ListLoadedModulesAsync(hostActionEntry, ct),
            ModuleDevOperation.ListWorkspaces =>
                await ListWorkspacesAsync(hostActionEntry, ct),
            _ => throw new NotSupportedException(
                $"Unknown ModuleDev operation: {action.Operation}"),
        };

        return new ModuleDevActionResult(content);
    }

    private async Task<string> ScaffoldModuleAsync(
        JsonElement parameters,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        RejectRuntimeRequest(parameters, "scaffold_module");
        var host = await GetHostModulesAsync(hostActionEntry, ct);
        BindWorkspace(host);

        List<ModuleScaffoldService.ToolStub>? tools = null;
        if (parameters.TryGetProperty("tools", out var toolsElement) &&
            toolsElement.ValueKind == JsonValueKind.Array)
        {
            tools = [];
            foreach (var tool in toolsElement.EnumerateArray())
            {
                tools.Add(new ModuleScaffoldService.ToolStub(
                    RequiredString(tool, "name"),
                    OptionalString(tool, "description"),
                    OptionalString(tool, "parameters_hint")));
            }
        }

        var result = await scaffold.ScaffoldAsync(
            new ModuleScaffoldService.ScaffoldSpec(
                RequiredString(parameters, "module_id"),
                RequiredString(parameters, "display_name"),
                RequiredString(parameters, "tool_prefix"),
                OptionalString(parameters, "description"),
                tools),
            host,
            ct);
        return Serialize(new { result.ModuleDir, result.Files });
    }

    private async Task<string> WriteFileAsync(
        JsonElement parameters,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        BindWorkspace(await GetHostModulesAsync(hostActionEntry, ct));
        var result = await workspace.WriteFileAsync(
            RequiredString(parameters, "module_id"),
            RequiredString(parameters, "relative_path"),
            RequiredString(parameters, "content"),
            ct);
        return Serialize(new { path = result.Path, bytes_written = result.BytesWritten });
    }

    private async Task<string> ReadFileAsync(
        JsonElement parameters,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        BindWorkspace(await GetHostModulesAsync(hostActionEntry, ct));
        return await workspace.ReadFileAsync(
            RequiredString(parameters, "module_id"),
            RequiredString(parameters, "relative_path"),
            OptionalInt(parameters, "max_lines") ?? 500,
            ct);
    }

    private async Task<string> ListFilesAsync(
        JsonElement parameters,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        BindWorkspace(await GetHostModulesAsync(hostActionEntry, ct));
        return Serialize(workspace.ListFiles(
            RequiredString(parameters, "module_id"),
            OptionalString(parameters, "include_pattern")));
    }

    private async Task<string> BuildModuleAsync(
        JsonElement parameters,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        BindWorkspace(await GetHostModulesAsync(hostActionEntry, ct));
        return Serialize(await build.BuildAsync(
            RequiredString(parameters, "module_id"),
            OptionalString(parameters, "configuration") ?? "Debug",
            ct));
    }

    private static async Task<string> ChangeModuleStateAsync(
        JsonElement parameters,
        HostModuleLifecycleOperation operation,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        var action = new HostModuleLifecycleAction(
            operation,
            RequiredString(parameters, "module_id"));
        var result = await InvokeCrossSidecarAsync(
            hostActionEntry,
            HostOperationActionDescriptors.ModuleLifecycle,
            action,
            ct);
        return Serialize(result);
    }

    private static async Task<string> TestToolAsync(
        JsonElement parameters,
        Guid? conversationId,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        var toolName = RequiredString(parameters, "tool_name");
        var arguments = parameters.TryGetProperty("parameters", out var argumentElement) &&
            argumentElement.ValueKind == JsonValueKind.Object
                ? argumentElement.Clone()
                : throw new ArgumentException("parameters must be an object.");
        var timeoutSeconds = OptionalInt(parameters, "timeout_seconds") ?? 30;
        if (timeoutSeconds is < 1 or > 180)
            throw new ArgumentOutOfRangeException(
                nameof(parameters),
                "timeout_seconds must be from 1 through 180.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await InvokeCrossSidecarAsync(
            hostActionEntry,
            HostOperationActionDescriptors.ToolInvoke,
            new HostToolInvokeAction(
                Guid.NewGuid(),
                conversationId,
                $"module-dev-{Guid.NewGuid():N}",
                toolName,
                arguments),
            timeout.Token);
        return Serialize(result);
    }

    private async Task<string> InspectProcessAsync(JsonElement parameters, CancellationToken ct)
    {
        var include = parameters.TryGetProperty("include", out var includeElement) &&
            includeElement.ValueKind == JsonValueKind.Array
                ? includeElement.EnumerateArray().Select(value =>
                    value.GetString() ?? throw new ArgumentException(
                        "Every include value must be a string.")).ToArray()
                : null;
        return await processInspection.InspectAsync(
            RequiredString(parameters, "target"),
            include,
            OptionalString(parameters, "export_filter"),
            ct);
    }

    private async Task<string> DiscoverComInterfacesAsync(
        JsonElement parameters,
        CancellationToken ct) =>
        await comInspection.InspectAsync(
            RequiredString(parameters, "typelib_path"),
            OptionalString(parameters, "interface_filter"),
            OptionalBool(parameters, "include_inherited") ?? false,
            ct);

    private async Task<string> EnumerateEnvironmentAsync(
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        var host = await GetHostModulesAsync(hostActionEntry, ct);
        BindWorkspace(host);
        return environment.ToJson(await environment.GetEnvironmentAsync(host, ct));
    }

    private string GetSdkReference(JsonElement parameters) =>
        sdkReference.GetReference(OptionalString(parameters, "topic") ?? "agent_workflow");

    private async Task<string> RecordSteeringAsync(
        JsonElement parameters,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        var result = await InvokeCrossSidecarAsync(
            hostActionEntry,
            ContextSteeringActionDescriptors.Record,
            CreateSteeringAction(parameters),
            ct);
        return Serialize(result);
    }

    private static async Task<string> ListSteeringAsync(
        JsonElement parameters,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        var result = await InvokeCrossSidecarAsync(
            hostActionEntry,
            ContextSteeringActionDescriptors.List,
            new ContextListSteeringAction(
                RequiredGuid(parameters, "channel_id"),
                OptionalGuid(parameters, "thread_id"),
                OptionalInt(parameters, "limit") ?? 20),
            ct);
        return Serialize(result);
    }

    private string DescribeModuleSystem() =>
        sdkReference.GetReference("all") + Environment.NewLine + """

            SharpClaw modules implement ISharpClawModule.
            A module registers services, contracts, storage, actions, events, hooks, tools, CLI commands, and endpoints through IServiceCollection.
            External modules use a sidecar manifest and authenticated host action entries.
            """;

    private static async Task<string> ListLoadedModulesAsync(
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        var modules = (await GetHostModulesAsync(hostActionEntry, ct)).Modules;
        return Serialize(modules.Select(module => new
        {
            module.State.SourceId,
            module.State.DisplayName,
            module.State.ToolPrefix,
            module.State.Enabled,
            module.State.Version,
            module.State.Registered,
            module.State.IsExternal,
            module.ExportedContractNames,
        }));
    }

    private async Task<string> ListWorkspacesAsync(
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        BindWorkspace(await GetHostModulesAsync(hostActionEntry, ct));
        var root = workspace.ExternalPackagesDirectory;
        return Serialize(Directory.Exists(root)
            ? Directory.GetDirectories(root)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : []);
    }

    private async Task<string> ApplyModuleFilesAsync(
        JsonElement parameters,
        Guid? conversationId,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        RejectRuntimeRequest(parameters, "apply_module_files");
        var host = await GetHostModulesAsync(hostActionEntry, ct);
        BindWorkspace(host);
        var SourceId = RequiredString(parameters, "module_id");
        var steeringParameters = parameters.TryGetProperty("conversation", out var conversation) &&
            conversation.ValueKind == JsonValueKind.Object
                ? conversation
                : throw new ArgumentException("conversation must be an object.");
        var written = new List<object>();

        try
        {
            var files = ReadFiles(parameters);
            foreach (var file in files)
                workspace.ValidateFileForWrite(SourceId, file.RelativePath, file.Content);
            foreach (var file in files)
            {
                var result = await workspace.WriteFileAsync(
                    SourceId,
                    file.RelativePath,
                    file.Content,
                    ct);
                written.Add(new
                {
                    relative_path = file.RelativePath,
                    path = result.Path,
                    bytes_written = result.BytesWritten,
                });
            }

            ModuleBuildService.BuildResult? buildResult = null;
            if (OptionalBool(parameters, "build") ?? true)
            {
                buildResult = await build.BuildAsync(
                    SourceId,
                    OptionalString(parameters, "configuration") ?? "Debug",
                    ct);
                if (!buildResult.Success)
                {
                    var steering = await RecordWorkflowSteeringAsync(
                        hostActionEntry,
                        steeringParameters,
                        "module_build",
                        $"Module '{SourceId}' build failed.",
                        FormatBuildDiagnostics(buildResult),
                        ct);
                    return Serialize(new
                    {
                        success = false,
                        module_id = SourceId,
                        runtime = ModuleScaffoldService.DotNetRuntime,
                        files = written,
                        build = buildResult,
                        steering,
                    });
                }
            }

            HostModuleLifecycleResult? lifecycle = null;
            if (OptionalBool(parameters, "load") ?? true)
            {
                var operation = host.Modules.Any(module =>
                    string.Equals(module.State.SourceId, SourceId, StringComparison.Ordinal))
                        ? HostModuleLifecycleOperation.Reload
                        : HostModuleLifecycleOperation.Load;
                lifecycle = await InvokeCrossSidecarAsync(
                    hostActionEntry,
                    HostOperationActionDescriptors.ModuleLifecycle,
                    new HostModuleLifecycleAction(operation, SourceId),
                    ct);
            }

            var tests = await RunWorkflowToolTestsAsync(
                parameters,
                conversationId,
                hostActionEntry,
                ct);
            var succeeded = tests.All(test => test.Success);
            var summary = succeeded
                ? $"Module '{SourceId}' workflow completed."
                : $"Module '{SourceId}' workflow completed with failed Tool checks.";
            var steeringResult = await RecordWorkflowSteeringAsync(
                hostActionEntry,
                steeringParameters,
                "module_workflow",
                summary,
                Serialize(new { files = written, build = buildResult, load = lifecycle, tests }),
                ct);
            return Serialize(new
            {
                success = succeeded,
                module_id = SourceId,
                runtime = ModuleScaffoldService.DotNetRuntime,
                files = written,
                build = buildResult,
                load = lifecycle,
                tests,
                steering = steeringResult,
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var steering = await RecordWorkflowSteeringAsync(
                hostActionEntry,
                steeringParameters,
                "module_workflow_error",
                $"Module '{SourceId}' workflow failed before completion.",
                Truncate(exception.ToString()),
                ct);
            return Serialize(new
            {
                success = false,
                module_id = SourceId,
                files = written,
                error = exception.Message,
                steering,
            });
        }
    }

    private static IReadOnlyList<ModuleFile> ReadFiles(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("files must be an array.");
        }

        var files = filesElement.EnumerateArray()
            .Select(file => new ModuleFile(
                RequiredString(file, "relative_path"),
                RequiredString(file, "content")))
            .ToArray();
        if (files.Length == 0)
            throw new ArgumentException("files must contain at least one item.");

        return files;
    }

    private async Task<IReadOnlyList<WorkflowToolResult>> RunWorkflowToolTestsAsync(
        JsonElement parameters,
        Guid? conversationId,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        if (!parameters.TryGetProperty("test_tools", out var testsElement) ||
            testsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<WorkflowToolResult>();
        foreach (var test in testsElement.EnumerateArray())
        {
            var toolName = RequiredString(test, "tool_name");
            var arguments = test.TryGetProperty("parameters", out var argumentElement) &&
                argumentElement.ValueKind == JsonValueKind.Object
                    ? argumentElement.Clone()
                    : EmptyObject();
            var timeoutSeconds = OptionalInt(test, "timeout_seconds") ?? 30;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                var outcome = await InvokeCrossSidecarAsync(
                    hostActionEntry,
                    HostOperationActionDescriptors.ToolInvoke,
                    new HostToolInvokeAction(
                        Guid.NewGuid(),
                        conversationId,
                        $"module-dev-{Guid.NewGuid():N}",
                        toolName,
                        arguments),
                    timeout.Token);
                results.Add(new WorkflowToolResult(toolName, true, outcome, null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                results.Add(new WorkflowToolResult(toolName, false, null, exception.Message));
            }
        }

        return results;
    }

    private static ContextRecordSteeringAction CreateSteeringAction(JsonElement parameters) =>
        new(
            RequiredGuid(parameters, "channel_id"),
            OptionalGuid(parameters, "thread_id"),
            OptionalString(parameters, "source") ?? "module_dev",
            OptionalString(parameters, "category") ?? "manual",
            RequiredString(parameters, "summary"),
            OptionalString(parameters, "details") is { } details ? Truncate(details) : null,
            OptionalString(parameters, "client_type") ?? "module-dev");

    private static Task<ContextSteeringRecord> RecordWorkflowSteeringAsync(
        IHostActionEntry hostActionEntry,
        JsonElement conversation,
        string category,
        string summary,
        string? details,
        CancellationToken ct) =>
        InvokeCrossSidecarAsync(
            hostActionEntry,
            ContextSteeringActionDescriptors.Record,
            new ContextRecordSteeringAction(
                RequiredGuid(conversation, "channel_id"),
                OptionalGuid(conversation, "thread_id"),
                OptionalString(conversation, "source") ?? "module_dev",
                category,
                summary,
                details is null ? null : Truncate(details),
                OptionalString(conversation, "client_type") ?? "module-dev"),
            ct);

    private static async Task<HostModuleListResult> GetHostModulesAsync(
        IHostActionEntry hostActionEntry,
        CancellationToken ct) =>
        await InvokeCrossSidecarAsync(
            hostActionEntry,
            HostOperationActionDescriptors.ModuleList,
            new HostModuleListAction(),
            ct);

    private void BindWorkspace(HostModuleListResult host)
    {
        ArgumentNullException.ThrowIfNull(host);
        workspace.BindExternalModulesDirectory(host.ExternalModulesDirectory);
    }

    private static async Task<TResult> InvokeCrossSidecarAsync<TAction, TResult>(
        IHostActionEntry hostActionEntry,
        ActionDescriptor<TAction, TResult> descriptor,
        TAction action,
        CancellationToken ct)
    {
        var outcome = await hostActionEntry.InvokeCrossSidecarAsync(
            new ModuleCrossSidecarActionEntryRequest<TAction, TResult>(descriptor, action),
            ct);
        if (outcome.Kind != ActionOutcomeKind.Completed || outcome.Result is null)
        {
            throw new InvalidOperationException(
                outcome.Error?.Message ?? $"Action '{descriptor.Key.Value}' did not complete.");
        }

        return outcome.Result;
    }

    private static string RequiredString(JsonElement parameters, string name) =>
        OptionalString(parameters, name)
        ?? throw new ArgumentException($"{name} is required.");

    private static string? OptionalString(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? OptionalInt(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static bool? OptionalBool(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static Guid RequiredGuid(JsonElement parameters, string name) =>
        OptionalGuid(parameters, name)
        ?? throw new ArgumentException($"{name} must contain one canonical non-empty GUID.");

    private static Guid? OptionalGuid(JsonElement parameters, string name)
    {
        var raw = OptionalString(parameters, name);
        if (raw is null)
            return null;
        return Guid.TryParseExact(raw, "D", out var value) && value != Guid.Empty
            ? value
            : throw new ArgumentException($"{name} must contain one canonical non-empty GUID.");
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static JsonElement EmptyObject() =>
        JsonSerializer.SerializeToElement(new { });

    private static string FormatBuildDiagnostics(ModuleBuildService.BuildResult result)
    {
        var diagnostics = result.Errors.Count > 0 ? result.Errors : result.Warnings;
        return diagnostics.Count == 0
            ? Truncate(result.RawOutput)
            : string.Join(
                Environment.NewLine,
                diagnostics.Select(item =>
                    $"{item.File}({item.Line},{item.Column}) {item.Code}: {item.Message}"));
    }

    private static string Truncate(string value) =>
        value.Length <= 15_000
            ? value
            : value[..15_000] + Environment.NewLine + "... truncated ...";

    internal static void RejectRuntimeRequest(JsonElement parameters, string toolName)
    {
        if (parameters.EnumerateObject().Any(property =>
            string.Equals(property.Name, "runtime", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"The '{toolName}' Tool accepts .NET modules only.");
        }
    }

    private sealed record ModuleFile(string RelativePath, string Content);

    private sealed record WorkflowToolResult(
        string ToolName,
        bool Success,
        ToolInvocationOutcome? Outcome,
        string? Error);
}

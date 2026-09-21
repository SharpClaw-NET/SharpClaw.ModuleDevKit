namespace SharpClaw.Modules.ModuleDev.Services;

internal sealed class SharpClawSdkReferenceService
{
    private static readonly IReadOnlyDictionary<string, string> Topics =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["agent_workflow"] = """
                SharpClaw SDK agent workflow.

                An agent can build without an IDE by treating ModuleDev as the
                workbench. Start with mdk_get_sdk_reference for the runtime and
                capability you need. Use mdk_scaffold_module for a new .NET
                workspace, or use mdk_apply_module_files when you know the file
                contents and want to write several files in one operation.
                The workflow writes the files, builds the .NET project, loads or
                reloads the module, and optionally invokes test tools. Build
                failures stop before hot-load and return structured compiler
                diagnostics directly to the caller.

                Example module workflow:

                ```json
                {
                  "module_id": "agent_notes",
                  "files": [
                    {
                      "relative_path": "AgentNotes.csproj",
                      "content": "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                    },
                    {
                      "relative_path": "AgentNotesModule.cs",
                      "content": "public sealed class AgentNotesModule {}"
                    }
                  ],
                  "build": true,
                  "load": true
                }
                ```
                """,

            ["dotnet"] = """
                SharpClaw .NET module SDK.

                A module implements ISharpClawModule. It registers services,
                contracts, storage, actions, events, hooks, Tools, and chat
                contributors through IServiceCollection. The same service
                collection registers neutral CLI and endpoint handlers.
                Keep module code behind the
                Contracts and ModuleSDK boundaries. Do not reference Runtime
                assemblies or a host DbContext.

                Minimal tool flow:

                ```csharp
                public void ConfigureServices(IServiceCollection services)
                {
                    services.AddTool<EchoToolHandler>(
                        new ToolDescriptor("echo", "Return text.", EmptySchema()));
                }

                public sealed class EchoToolHandler : IToolHandler
                {
                    public ValueTask<ToolResult> InvokeAsync(
                        ToolInvocation invocation,
                        CancellationToken cancellationToken) =>
                        ValueTask.FromResult(ToolResult.Text(
                            invocation.Arguments.GetProperty("text").GetString() ?? ""));
                }
                ```
                """,

            ["storage"] = """
                SharpClaw module storage SDK.

                Storage is host-owned. Modules declare storage contracts in
                their module graph. Sidecars use IScopedStorageGateway for get,
                upsert, batch upsert, delete, batch delete, list, query, and claim.
                Query and claim operate on declared indexes rather than leaking
                EF Core or LINQ execution into sidecars. Use query builders for
                simple index filters and use claim when a job-like record must
                be atomically selected and patched.
                """,

            ["manifest"] = """
                SharpClaw module manifest SDK.

                Each external module workspace has package.json. .NET modules
                use runtime dotnet, entryAssembly, and entryType. The host
                uses id, displayName, version, toolPrefix, enabled, hostMode,
                exports, and requires during discovery and load.

                Keep id and toolPrefix stable once a module is loaded. Changing
                either value means the host treats the module as a different
                contribution surface.
                """,
        };

    public IReadOnlyList<string> TopicNames => Topics.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public string GetReference(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic) || topic.Equals("all", StringComparison.OrdinalIgnoreCase))
            return string.Join(
                $"{Environment.NewLine}{Environment.NewLine}",
                TopicNames.Select(name => Topics[name]));

        return Topics.TryGetValue(topic.Trim(), out var text)
            ? text
            : throw new ArgumentException(
                $"Unknown SDK reference topic '{topic}'. Available topics: {string.Join(", ", TopicNames)}.");
    }
}

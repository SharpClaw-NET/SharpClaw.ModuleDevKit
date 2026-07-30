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
                reloads the module, optionally invokes test tools, and writes a
                system-role conversation steering message for the next turn.
                Build failures stop before hot-load, so the next turn receives
                structured compiler diagnostics.

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
                  "load": true,
                  "conversation": {
                    "channel_id": "00000000-0000-0000-0000-000000000000",
                    "thread_id": "11111111-1111-1111-1111-111111111111"
                  }
                }
                ```
                """,

            ["dotnet"] = """
                SharpClaw .NET module SDK.

                The .NET SDK surface is SharpClaw.Contracts. A module implements
                ISharpClawCoreModule and returns descriptors for tools, inline
                tools, contracts, resources, flags, header tags, endpoints, and
                CLI commands. Keep module code behind SharpClaw.Contracts
                interfaces and explicit package references. Do not reference
                SharpClaw.Runtime.BLL, SharpClaw.Runtime.INF, or a host
                DbContext from a module. Host-owned features such as lifecycle,
                module storage, and conversation steering use Contracts
                interfaces.

                Minimal tool flow:

                ```csharp
                public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
                [
                    new("echo", "Return text.", EmptySchema(),
                        new ModuleToolPermission(false, null, null))
                ];

                public Task<string> ExecuteToolAsync(
                    string toolName,
                    JsonElement parameters,
                    AgentJobContext job,
                    IServiceProvider sp,
                    CancellationToken ct) =>
                    toolName == "echo"
                        ? Task.FromResult(parameters.GetProperty("text").GetString() ?? "")
                        : throw new NotSupportedException(toolName);
                ```
                """,

            ["storage"] = """
                SharpClaw module storage SDK.

                Storage is host-owned. Modules declare storage contracts in
                discovery and call the host capability server for get, upsert,
                batchUpsert, delete, batchDelete, list, query, and claim.
                Query and claim operate on declared indexes rather than leaking
                EF Core or LINQ execution into sidecars. Use query builders for
                simple index filters and use claim when a job-like record must
                be atomically selected and patched.
                """,

            ["conversation_steering"] = """
                SharpClaw conversation steering SDK.

                Conversation steering is a host capability that writes a
                persisted system-role chat message into a channel or thread.
                The next model turn sees it through the normal thread history
                path. Use it after build failures, successful hot-loads, test
                results, and other results that should guide the next message.
                The host validates the channel and thread relationship, stores
                source and category metadata, and publishes thread activity
                when the target is threaded.
                """,

            ["manifest"] = """
                SharpClaw module manifest SDK.

                Each external module workspace has module.json. .NET modules
                use runtime dotnet, entryAssembly, and moduleType. The host
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

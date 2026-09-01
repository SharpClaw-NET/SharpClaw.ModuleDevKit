using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.Modules.ModuleDev;

/// <summary>Identifies one ModuleDev operation.</summary>
public enum ModuleDevOperation
{
    ScaffoldModule,
    WriteFile,
    ReadFile,
    ListFiles,
    BuildModule,
    LoadModule,
    UnloadModule,
    ReloadModule,
    TestTool,
    InspectProcess,
    DiscoverComInterfaces,
    EnumerateDevEnvironment,
    GetSdkReference,
    ApplyModuleFiles,
    RecordConversationSteering,
    ListConversationSteering,
    DescribeModuleSystem,
    ListLoadedModules,
    ListWorkspaces,
}

/// <summary>Requests one typed ModuleDev operation.</summary>
public sealed record ModuleDevAction(
    ModuleDevOperation Operation,
    JsonElement Parameters,
    Guid? ConversationId = null);

/// <summary>Contains one completed ModuleDev result.</summary>
public sealed record ModuleDevActionResult(string Content);

/// <summary>Defines the public ModuleDev actions and Tools.</summary>
public static class ModuleDevContracts
{
    public const string ModuleId = "sharpclaw_module_dev";

    public static Guid ReadTerminalId { get; } =
        new("f2f128d4-371a-4c88-a296-c16f60e9a101");

    public static Guid MutationTerminalId { get; } =
        new("f2f128d4-371a-4c88-a296-c16f60e9a102");

    private static readonly ActionInterceptionCapabilities Capabilities =
        ActionInterceptionCapabilities.Inspect |
        ActionInterceptionCapabilities.Cancel |
        ActionInterceptionCapabilities.Observe;

    private static readonly ActionRepeatPolicy ReadRepeatPolicy =
        new(ActionRepeatKind.Idempotent, 1, TimeSpan.Zero, "module-dev.read");

    private static readonly ActionRepeatPolicy MutationRepeatPolicy =
        new(ActionRepeatKind.None, 1, TimeSpan.Zero, "module-dev.mutation");

    public static ActionDescriptor<ModuleDevAction, ModuleDevActionResult> ReadDescriptor { get; } =
        CreateDescriptor("module-dev.read", "module-dev.read", false, ReadRepeatPolicy);

    public static ActionDescriptor<ModuleDevAction, ModuleDevActionResult> MutationDescriptor { get; } =
        CreateDescriptor("module-dev.mutate", "module-dev.mutation", true, MutationRepeatPolicy);

    public static IReadOnlyList<ToolDescriptor> ToolDescriptors =>
    [
        Tool("scaffold_module", "Create a .NET module project.", ScaffoldSchema),
        Tool("write_file", "Write one module workspace file.", WriteFileSchema),
        Tool("read_file", "Read one module workspace file.", ReadFileSchema),
        Tool("list_files", "List files in one module workspace.", ListFilesSchema),
        Tool("build_module", "Build one module project.", BuildModuleSchema),
        Tool("load_module", "Load one module into the active host graph.", ModuleIdSchema),
        Tool("unload_module", "Unload one module from the active host graph.", ModuleIdSchema),
        Tool("test_tool", "Invoke one loaded Tool through the host Tool pipeline.", TestToolSchema),
        Tool("inspect_process", "Inspect one local process.", InspectProcessSchema),
        Tool("discover_com_interfaces", "Inspect one COM type library.", DiscoverComSchema),
        Tool("enumerate_dev_environment", "Report the module development environment.", EmptySchema),
        Tool("get_sdk_reference", "Return the SharpClaw module SDK reference.", SdkReferenceSchema),
        Tool("apply_module_files", "Apply, build, load, and test one module update.", ApplyFilesSchema),
        Tool("record_conversation_steering", "Record one Context steering entry.", RecordSteeringSchema),
        Tool("list_conversation_steering", "List Context steering entries.", ListSteeringSchema),
        Tool("describe_module_system", "Describe the current module system.", EmptySchema),
        Tool("list_loaded_modules", "List modules in the active host graph.", EmptySchema),
    ];

    public static bool IsRead(ModuleDevOperation operation) => operation is
        ModuleDevOperation.ReadFile or
        ModuleDevOperation.ListFiles or
        ModuleDevOperation.InspectProcess or
        ModuleDevOperation.DiscoverComInterfaces or
        ModuleDevOperation.EnumerateDevEnvironment or
        ModuleDevOperation.GetSdkReference or
        ModuleDevOperation.ListConversationSteering or
        ModuleDevOperation.DescribeModuleSystem or
        ModuleDevOperation.ListLoadedModules or
        ModuleDevOperation.ListWorkspaces;

    public static ModuleDevOperation OperationForTool(string toolName) => toolName switch
    {
        "scaffold_module" => ModuleDevOperation.ScaffoldModule,
        "write_file" => ModuleDevOperation.WriteFile,
        "read_file" => ModuleDevOperation.ReadFile,
        "list_files" => ModuleDevOperation.ListFiles,
        "build_module" => ModuleDevOperation.BuildModule,
        "load_module" => ModuleDevOperation.LoadModule,
        "unload_module" => ModuleDevOperation.UnloadModule,
        "test_tool" => ModuleDevOperation.TestTool,
        "inspect_process" => ModuleDevOperation.InspectProcess,
        "discover_com_interfaces" => ModuleDevOperation.DiscoverComInterfaces,
        "enumerate_dev_environment" => ModuleDevOperation.EnumerateDevEnvironment,
        "get_sdk_reference" => ModuleDevOperation.GetSdkReference,
        "apply_module_files" => ModuleDevOperation.ApplyModuleFiles,
        "record_conversation_steering" => ModuleDevOperation.RecordConversationSteering,
        "list_conversation_steering" => ModuleDevOperation.ListConversationSteering,
        "describe_module_system" => ModuleDevOperation.DescribeModuleSystem,
        "list_loaded_modules" => ModuleDevOperation.ListLoadedModules,
        _ => throw new NotSupportedException($"Unknown ModuleDev Tool: {toolName}"),
    };

    private static ActionDescriptor<ModuleDevAction, ModuleDevActionResult> CreateDescriptor(
        string keyValue,
        string category,
        bool irreversible,
        ActionRepeatPolicy repeatPolicy)
    {
        var key = new SharpClawActionKey(keyValue);
        return new ActionDescriptor<ModuleDevAction, ModuleDevActionResult>(
            key,
            1,
            category,
            Capabilities,
            ContainsSensitiveData: true,
            HasIrreversibleEffects: irreversible,
            repeatPolicy,
            ContinuationPolicy: null,
            DefaultTimeout: TimeSpan.FromMinutes(3))
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = [ActionSafePoint.BeforeTerminal, ActionSafePoint.AfterTerminal],
            InputSchema = ModuleSchemaIdentity.ActionInput(key, 1, typeof(ModuleDevAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(key, 1, typeof(ModuleDevActionResult)),
        };
    }

    private static ToolDescriptor Tool(string name, string description, JsonElement schema) =>
        new(name, description, schema);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement EmptySchema { get; } = Parse("""
        { "type": "object", "properties": {}, "additionalProperties": false }
        """);

    private static JsonElement ScaffoldSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "string" },
            "display_name": { "type": "string" },
            "tool_prefix": { "type": "string" },
            "description": { "type": "string" },
            "tools": { "type": "array", "items": { "type": "object" } }
          },
          "required": ["module_id", "display_name", "tool_prefix"],
          "additionalProperties": false
        }
        """);

    private static JsonElement WriteFileSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "string" },
            "relative_path": { "type": "string" },
            "content": { "type": "string" }
          },
          "required": ["module_id", "relative_path", "content"],
          "additionalProperties": false
        }
        """);

    private static JsonElement ReadFileSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "string" },
            "relative_path": { "type": "string" },
            "max_lines": { "type": "integer" }
          },
          "required": ["module_id", "relative_path"],
          "additionalProperties": false
        }
        """);

    private static JsonElement ListFilesSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "string" },
            "include_pattern": { "type": "string" }
          },
          "required": ["module_id"],
          "additionalProperties": false
        }
        """);

    private static JsonElement BuildModuleSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "string" },
            "configuration": { "type": "string", "enum": ["Debug", "Release"] }
          },
          "required": ["module_id"],
          "additionalProperties": false
        }
        """);

    private static JsonElement ModuleIdSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": { "module_id": { "type": "string" } },
          "required": ["module_id"],
          "additionalProperties": false
        }
        """);

    private static JsonElement TestToolSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": {
            "tool_name": { "type": "string" },
            "parameters": { "type": "object" },
            "timeout_seconds": { "type": "integer" }
          },
          "required": ["tool_name", "parameters"],
          "additionalProperties": false
        }
        """);

    private static JsonElement InspectProcessSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": {
            "target": { "type": "string" },
            "include": { "type": "array", "items": { "type": "string" } },
            "export_filter": { "type": "string" }
          },
          "required": ["target"],
          "additionalProperties": false
        }
        """);

    private static JsonElement DiscoverComSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": {
            "typelib_path": { "type": "string" },
            "interface_filter": { "type": "string" },
            "include_inherited": { "type": "boolean" }
          },
          "required": ["typelib_path"],
          "additionalProperties": false
        }
        """);

    private static JsonElement SdkReferenceSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": {
            "topic": {
              "type": "string",
              "enum": ["agent_workflow", "dotnet", "storage", "conversation_steering", "manifest", "all"]
            }
          },
          "additionalProperties": false
        }
        """);

    private static JsonElement RecordSteeringSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": {
            "channel_id": { "type": "string" },
            "thread_id": { "type": "string" },
            "summary": { "type": "string" },
            "details": { "type": "string" },
            "source": { "type": "string" },
            "category": { "type": "string" },
            "client_type": { "type": "string" }
          },
          "required": ["channel_id", "summary"],
          "additionalProperties": false
        }
        """);

    private static JsonElement ListSteeringSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": {
            "channel_id": { "type": "string" },
            "thread_id": { "type": "string" },
            "limit": { "type": "integer" }
          },
          "required": ["channel_id"],
          "additionalProperties": false
        }
        """);

    private static JsonElement ApplyFilesSchema { get; } = Parse("""
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "string" },
            "configuration": { "type": "string", "enum": ["Debug", "Release"] },
            "build": { "type": "boolean" },
            "load": { "type": "boolean" },
            "files": { "type": "array", "items": { "type": "object" } },
            "test_tools": { "type": "array", "items": { "type": "object" } },
            "conversation": { "type": "object" }
          },
          "required": ["module_id", "files", "conversation"],
          "additionalProperties": false
        }
        """);
}

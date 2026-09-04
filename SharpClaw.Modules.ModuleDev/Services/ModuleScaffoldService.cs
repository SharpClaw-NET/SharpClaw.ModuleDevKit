using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using SharpClaw.ModuleSDK.HostOperations;

namespace SharpClaw.Modules.ModuleDev.Services;

/// <summary>
/// Generates module project files from a specification using embedded templates.
/// </summary>
internal sealed partial class ModuleScaffoldService(ModuleWorkspaceService workspace)
{
    internal const string DotNetRuntime = "dotnet";

    /// <summary>
    /// Scaffold specification provided by the agent.
    /// </summary>
    internal sealed record ScaffoldSpec(
        string SourceId,
        string DisplayName,
        string ToolPrefix,
        string? Description = null,
        IReadOnlyList<ToolStub>? Tools = null,
        IReadOnlyList<string>? ContractsRequired = null,
        IReadOnlyList<string>? ContractsExported = null,
        IReadOnlyList<string>? Platforms = null);

    internal sealed record ToolStub(
        string Name,
        string? Description = null,
        string? ParametersHint = null);

    /// <summary>
    /// Scaffold result returned to the caller.
    /// </summary>
    internal sealed record ScaffoldResult(string ModuleDir, IReadOnlyList<string> Files);

    /// <summary>
    /// Generate a complete module project from a spec.
    /// </summary>
    public async Task<ScaffoldResult> ScaffoldAsync(
        ScaffoldSpec spec,
        HostModuleListResult host,
        CancellationToken ct = default)
    {
        ValidateSpec(spec, host);

        var moduleDir = workspace.ResolveModuleDir(spec.SourceId);
        Directory.CreateDirectory(moduleDir);

        return await ScaffoldDotNetAsync(spec, moduleDir, ct);
    }

    private async Task<ScaffoldResult> ScaffoldDotNetAsync(
        ScaffoldSpec spec, string moduleDir, CancellationToken ct)
    {
        var files = new List<string>();
        var assemblyName = ToPascalCase(spec.SourceId);

        // 1. Generate .csproj
        var csprojContent = LoadTemplate("ProjectFile.csproj.template")
            .Replace("{{ASSEMBLY_NAME}}", assemblyName)
            .Replace("{{DESCRIPTION}}", spec.Description ?? $"{spec.DisplayName} SharpClaw module.");

        var csprojName = ToPascalCase(spec.SourceId) + ".csproj";
        await workspace.WriteFileAsync(spec.SourceId, csprojName, csprojContent, ct);
        files.Add(csprojName);

        // 2. Generate module class
        var className = ToPascalCase(spec.SourceId) + "Module";
        var ns = ToPascalCase(spec.SourceId);
        var toolStubs = BuildToolStubs(spec.Tools);
        var toolDispatch = BuildToolDispatch(spec.Tools);

        var moduleContent = LoadTemplate("ModuleClass.cs.template")
            .Replace("{{NAMESPACE}}", ns)
            .Replace("{{CLASS_NAME}}", className)
            .Replace("{{MODULE_ID}}", spec.SourceId)
            .Replace("{{DISPLAY_NAME}}", spec.DisplayName)
            .Replace("{{TOOL_PREFIX}}", spec.ToolPrefix)
            .Replace("{{TOOL_STUBS}}", toolStubs)
            .Replace("{{TOOL_DISPATCH}}", toolDispatch);

        var moduleFileName = className + ".cs";
        await workspace.WriteFileAsync(spec.SourceId, moduleFileName, moduleContent, ct);
        files.Add(moduleFileName);

        // 3. Generate package.json
        var manifestContent = LoadTemplate("Manifest.json.template")
            .Replace("{{MODULE_ID}}", spec.SourceId)
            .Replace("{{DISPLAY_NAME}}", spec.DisplayName)
            .Replace("{{TOOL_PREFIX}}", spec.ToolPrefix)
            .Replace("{{NAMESPACE}}", ns)
            .Replace("{{CLASS_NAME}}", className)
            .Replace("{{ASSEMBLY_NAME}}", assemblyName)
            .Replace("{{DESCRIPTION}}", spec.Description ?? "");

        await workspace.WriteFileAsync(spec.SourceId, "package.json", manifestContent, ct);
        files.Add("package.json");

        return new ScaffoldResult(moduleDir, files);
    }

    // ── Validation ────────────────────────────────────────────────

    private static void ValidateSpec(ScaffoldSpec spec, HostModuleListResult host)
    {
        if (!ModuleIdRegex().IsMatch(spec.SourceId))
            throw new ArgumentException(
                $"Invalid module ID '{spec.SourceId}'. Must match ^[a-z][a-z0-9_]{{0,39}}$.");

        if (!ToolPrefixRegex().IsMatch(spec.ToolPrefix))
            throw new ArgumentException(
                $"Invalid tool prefix '{spec.ToolPrefix}'. Must match ^[a-z][a-z0-9]{{0,19}}$.");

        if (string.IsNullOrWhiteSpace(spec.DisplayName))
            throw new ArgumentException("Display name is required.");

        if (host.Modules.Any(module =>
            string.Equals(module.State.SourceId, spec.SourceId, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"Module ID '{spec.SourceId}' is already registered.");

        if (host.Modules.Any(module =>
            string.Equals(module.State.ToolPrefix, spec.ToolPrefix, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"Tool prefix '{spec.ToolPrefix}' is already in use.");
    }

    // ── Template helpers ──────────────────────────────────────────

    private static string LoadTemplate(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"{assembly.GetName().Name}.Templates.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded template not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string BuildToolStubs(IReadOnlyList<ToolStub>? tools)
    {
        if (tools is null or { Count: 0 })
            return "        // Add ToolDescriptor entries here.";

        var sb = new StringBuilder();
        foreach (var tool in tools)
        {
            var desc = tool.Description ?? $"TODO: describe {tool.Name}";
            ValidateToolName(tool.Name);
            sb.AppendLine($"        new(\"{tool.Name}\",");
            sb.AppendLine($"            \"{EscapeString(desc)}\",");
            sb.AppendLine("            ToolSchemas.EmptyObject),");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildToolDispatch(IReadOnlyList<ToolStub>? tools)
    {
        if (tools is null or { Count: 0 })
            return "            // Add Tool handlers here.";

        var sb = new StringBuilder();
        foreach (var tool in tools)
        {
            ValidateToolName(tool.Name);
            sb.AppendLine(
                $"            \"{tool.Name}\" => ValueTask.FromResult(ToolResult.Text(\"TODO: implement {EscapeString(tool.Name)}\")),");
        }

        return sb.ToString().TrimEnd();
    }

    private static string ToPascalCase(string snakeCase)
    {
        return string.Concat(
            snakeCase.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static string EscapeString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void ValidateToolName(string toolName)
    {
        if (!ToolNameRegex().IsMatch(toolName))
            throw new ArgumentException(
                $"Invalid Tool name '{toolName}'. Must contain a canonical identifier.");
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex ModuleIdRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9]{0,19}$")]
    private static partial Regex ToolPrefixRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex ToolNameRegex();
}

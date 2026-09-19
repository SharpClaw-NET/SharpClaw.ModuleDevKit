using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using SharpClaw.ModuleSDK;
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
        IReadOnlyList<ToolStub>? Tools = null);

    internal sealed record ToolStub(
        string Name,
        string? Description = null);

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
            .Replace("{{DESCRIPTION}}", spec.Description ?? $"{spec.DisplayName} SharpClaw module.")
            .Replace("{{MODULE_SDK_VERSION}}", PackageVersion(typeof(ISharpClawModule).Assembly));

        var csprojName = ToPascalCase(spec.SourceId) + ".csproj";
        await workspace.WriteFileAsync(spec.SourceId, csprojName, csprojContent, ct);
        files.Add(csprojName);

        // 2. Generate module class
        var className = ToPascalCase(spec.SourceId) + "Module";
        var ns = ToPascalCase(spec.SourceId);
        var toolDescriptors = BuildToolDescriptors(spec.Tools);
        var toolRegistrations = BuildToolRegistrations(spec.Tools);
        var toolHandlers = BuildToolHandlers(spec.Tools);

        var moduleContent = LoadTemplate("ModuleClass.cs.template")
            .Replace("{{NAMESPACE}}", ns)
            .Replace("{{CLASS_NAME}}", className)
            .Replace("{{MODULE_ID}}", spec.SourceId)
            .Replace("{{DISPLAY_NAME}}", spec.DisplayName)
            .Replace("{{TOOL_PREFIX}}", spec.ToolPrefix)
            .Replace("{{TOOL_DESCRIPTORS}}", toolDescriptors)
            .Replace("{{TOOL_REGISTRATIONS}}", toolRegistrations)
            .Replace("{{TOOL_HANDLERS}}", toolHandlers);

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

        var readmeContent = LoadTemplate("Readme.md.template")
            .Replace("{{DISPLAY_NAME}}", spec.DisplayName)
            .Replace("{{DESCRIPTION}}", spec.Description ?? $"{spec.DisplayName} SharpClaw package.")
            .Replace("{{TOOL_PREFIX}}", spec.ToolPrefix)
            .Replace("{{NAMESPACE}}", ns)
            .Replace("{{CLASS_NAME}}", className);

        await workspace.WriteFileAsync(spec.SourceId, "README.md", readmeContent, ct);
        files.Add("README.md");

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

        var tools = spec.Tools ?? [];
        foreach (var tool in tools)
            ValidateToolName(tool.Name);
        if (tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new ArgumentException("Each generated Tool requires a unique name.");
        if (tools.Select(tool => ToPascalCase(tool.Name)).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new ArgumentException("Each generated Tool requires a unique handler type name.");
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

    private static string BuildToolDescriptors(IReadOnlyList<ToolStub>? tools)
    {
        if (tools is null or { Count: 0 })
            return "        // Add ToolDescriptor entries here.";

        var sb = new StringBuilder();
        foreach (var tool in tools)
        {
            var desc = tool.Description ?? $"TODO: describe {tool.Name}";
            var property = ToPascalCase(tool.Name);
            sb.AppendLine($"    public static ToolDescriptor {property} {{ get; }} = new(");
            sb.AppendLine($"        \"{tool.Name}\",");
            sb.AppendLine($"        \"{EscapeString(desc)}\",");
            sb.AppendLine("        ToolSchemas.EmptyObject);");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildToolRegistrations(IReadOnlyList<ToolStub>? tools)
    {
        if (tools is null or { Count: 0 })
            return "        // Register package services here.";

        var sb = new StringBuilder();
        foreach (var tool in tools)
        {
            var type = ToPascalCase(tool.Name) + "Tool";
            var descriptor = ToPascalCase(tool.Name);
            sb.AppendLine($"        services.AddTool<{type}>(GeneratedTools.{descriptor});");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildToolHandlers(IReadOnlyList<ToolStub>? tools)
    {
        if (tools is null or { Count: 0 })
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var tool in tools)
        {
            var type = ToPascalCase(tool.Name) + "Tool";
            sb.AppendLine($"public sealed class {type} : IToolHandler");
            sb.AppendLine("{");
            sb.AppendLine("    public ValueTask<ToolResult> InvokeAsync(");
            sb.AppendLine("        ToolInvocation invocation,");
            sb.AppendLine("        CancellationToken cancellationToken)");
            sb.AppendLine("    {");
            sb.AppendLine("        cancellationToken.ThrowIfCancellationRequested();");
            sb.AppendLine($"        return ValueTask.FromResult(ToolResult.Text(\"TODO: implement {EscapeString(tool.Name)}\"));");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string PackageVersion(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            throw new InvalidOperationException("The SharpClaw ModuleSDK version is unavailable.");
        return informational.Split('+', 2)[0];
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

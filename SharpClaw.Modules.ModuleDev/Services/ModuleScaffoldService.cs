using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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
        return await ScaffoldDotNetAsync(spec, moduleDir, ct);
    }

    private async Task<ScaffoldResult> ScaffoldDotNetAsync(
        ScaffoldSpec spec, string moduleDir, CancellationToken ct)
    {
        var assemblyName = ToPascalCase(spec.SourceId);
        var csprojName = ToPascalCase(spec.SourceId) + ".csproj";
        var className = ToPascalCase(spec.SourceId) + "Module";
        var ns = ToPascalCase(spec.SourceId);
        var toolDescriptors = BuildToolDescriptors(spec.Tools);
        var toolRegistrations = BuildToolRegistrations(spec.Tools);
        var toolHandlers = BuildToolHandlers(spec.Tools);
        var moduleContent = LoadTemplate("ModuleClass.cs.template")
            .Replace("{{NAMESPACE}}", ns)
            .Replace("{{CLASS_NAME}}", className)
            .Replace("{{MODULE_ID}}", EscapeCSharpString(spec.SourceId))
            .Replace("{{DISPLAY_NAME}}", EscapeCSharpString(spec.DisplayName))
            .Replace("{{TOOL_PREFIX}}", EscapeCSharpString(spec.ToolPrefix))
            .Replace("{{TOOL_DESCRIPTORS}}", toolDescriptors)
            .Replace("{{TOOL_REGISTRATIONS}}", toolRegistrations)
            .Replace("{{TOOL_HANDLERS}}", toolHandlers);
        var moduleFileName = className + ".cs";
        var readmeContent = LoadTemplate("Readme.md.template")
            .Replace("{{DISPLAY_NAME}}", spec.DisplayName)
            .Replace("{{DESCRIPTION}}", spec.Description ?? $"{spec.DisplayName} SharpClaw package.")
            .Replace("{{TOOL_PREFIX}}", spec.ToolPrefix)
            .Replace("{{NAMESPACE}}", ns)
            .Replace("{{CLASS_NAME}}", className);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [csprojName] = BuildProjectFile(spec, assemblyName),
            [moduleFileName] = moduleContent,
            ["package.json"] = BuildManifest(spec, assemblyName, ns, className),
            ["README.md"] = readmeContent,
        };

        await workspace.WriteFilesAtomicallyAsync(spec.SourceId, files, ct);
        return new ScaffoldResult(moduleDir, files.Keys.ToArray());
    }

    // ── Validation ────────────────────────────────────────────────

    private static void ValidateSpec(ScaffoldSpec spec, HostModuleListResult host)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(host);

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
            sb.AppendLine($"        \"{EscapeCSharpString(tool.Name)}\",");
            sb.AppendLine($"        \"{EscapeCSharpString(desc)}\",");
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
            sb.AppendLine($"        return ValueTask.FromResult(ToolResult.Text(\"TODO: implement {EscapeCSharpString(tool.Name)}\"));");
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

    private static string BuildProjectFile(ScaffoldSpec spec, string assemblyName)
    {
        var content = LoadTemplate("ProjectFile.csproj.template")
            .Replace("{{ASSEMBLY_NAME}}", assemblyName)
            .Replace("{{DESCRIPTION}}", string.Empty)
            .Replace("{{MODULE_SDK_VERSION}}", PackageVersion(typeof(ISharpClawModule).Assembly));
        var document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        var description = document.Root?
            .Elements("PropertyGroup")
            .SelectMany(group => group.Elements("Description"))
            .SingleOrDefault()
            ?? throw new InvalidDataException("The project template has no Description element.");
        description.Value = spec.Description ?? $"{spec.DisplayName} SharpClaw module.";
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildManifest(
        ScaffoldSpec spec,
        string assemblyName,
        string ns,
        string className)
    {
        var manifest = new JsonObject
        {
            ["id"] = spec.SourceId,
            ["displayName"] = spec.DisplayName,
            ["version"] = "0.1.0-beta",
            ["toolPrefix"] = spec.ToolPrefix,
            ["runtime"] = DotNetRuntime,
            ["hostMode"] = "sidecar",
            ["entryAssembly"] = $"{assemblyName}.dll",
            ["entryType"] = $"{ns}.{className}",
            ["description"] = spec.Description ?? string.Empty,
            ["platforms"] = null,
            ["enabled"] = true,
            ["executionTimeoutSeconds"] = 60,
            ["exports"] = new JsonArray(),
            ["requires"] = new JsonArray(),
        };
        return manifest.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        });
    }

    private static string EscapeCSharpString(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': escaped.Append("\\\\"); break;
                case '"': escaped.Append("\\\""); break;
                case '\0': escaped.Append("\\0"); break;
                case '\a': escaped.Append("\\a"); break;
                case '\b': escaped.Append("\\b"); break;
                case '\f': escaped.Append("\\f"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                case '\t': escaped.Append("\\t"); break;
                case '\v': escaped.Append("\\v"); break;
                case '\u2028':
                case '\u2029':
                    escaped.Append($"\\u{(int)character:X4}");
                    break;
                default:
                    if (char.IsControl(character))
                        escaped.Append($"\\u{(int)character:X4}");
                    else
                        escaped.Append(character);
                    break;
            }
        }

        return escaped.ToString();
    }

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

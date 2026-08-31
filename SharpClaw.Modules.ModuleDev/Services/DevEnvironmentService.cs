using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK.HostOperations;

namespace SharpClaw.Modules.ModuleDev.Services;

/// <summary>
/// Queries the local development environment: installed SDKs, runtimes,
/// global tools, contracts assembly info, and loaded module metadata.
/// </summary>
internal sealed class DevEnvironmentService
{
    internal sealed record DevEnvironmentInfo(
        IReadOnlyList<string> DotnetSdks,
        IReadOnlyList<string> DotnetRuntimes,
        IReadOnlyList<string> GlobalTools,
        string ContractsAssemblyVersion,
        string ContractsAssemblyPath,
        string HostVersion,
        IReadOnlyList<RegisteredModuleInfo> RegisteredModules,
        string ExternalModulesDir);

    internal sealed record RegisteredModuleInfo(
        string Id,
        string Prefix,
        IReadOnlyList<string> ExportedContracts);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Gather full development environment information.
    /// </summary>
    public async Task<DevEnvironmentInfo> GetEnvironmentAsync(
        HostModuleListResult host,
        CancellationToken ct = default)
    {
        var sdksTask = RunDotnetCommandAsync("--list-sdks", ct);
        var runtimesTask = RunDotnetCommandAsync("--list-runtimes", ct);
        var toolsTask = RunDotnetCommandAsync("tool list -g", ct);

        await Task.WhenAll(sdksTask, runtimesTask, toolsTask);

        var sdks = ParseLines(await sdksTask);
        var runtimes = ParseLines(await runtimesTask);
        var tools = ParseLines(await toolsTask);

        var contractsAssembly = typeof(ISharpClawModule).Assembly;
        var contractsVersion = contractsAssembly.GetName().Version?.ToString() ?? "unknown";
        var contractsPath = contractsAssembly.Location;

        var hostAssembly = Assembly.GetEntryAssembly();
        var hostVersion = hostAssembly?.GetName().Version?.ToString() ?? "unknown";

        var modules = host.Modules.Select(module => new RegisteredModuleInfo(
            module.State.ModuleId,
            module.State.ToolPrefix,
            module.ExportedContractNames)).ToArray();

        return new DevEnvironmentInfo(
            DotnetSdks: sdks,
            DotnetRuntimes: runtimes,
            GlobalTools: tools,
            ContractsAssemblyVersion: contractsVersion,
            ContractsAssemblyPath: contractsPath,
            HostVersion: hostVersion,
            RegisteredModules: modules,
            ExternalModulesDir: host.ExternalModulesDirectory);
    }

    /// <summary>
    /// Serialize environment info to JSON.
    /// </summary>
    public string ToJson(DevEnvironmentInfo info) =>
        JsonSerializer.Serialize(info, JsonOpts);

    // ── Helpers ───────────────────────────────────────────────────

    private static async Task<string> RunDotnetCommandAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return output;
    }

    private static IReadOnlyList<string> ParseLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
}

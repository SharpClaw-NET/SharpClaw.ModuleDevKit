using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Modules.ModuleDev.Services;

namespace SharpClaw.Modules.ModuleDev.Handlers;

/// <summary>Runs the ModuleDev CLI through typed action authority.</summary>
internal sealed class ModuleDevCliHandler(
    IHostActionEntry hostActionEntry,
    ModuleDevActionGateway gateway) : ICliHandler
{
    public static CliCommandDescriptor Descriptor { get; } = new(
        "mdk",
        ["module-dev"],
        "Manage module development workspaces and active modules.",
        new JsonSchemaReference("sharpclaw.module-dev.cli.arguments", 1),
        new JsonSchemaReference("sharpclaw.module-dev.cli.result", 1),
        RequiresAdministrator: true);

    public async ValueTask<CliResult> ExecuteAsync(
        CliInvocation invocation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        try
        {
            var action = Parse(invocation.Arguments);
            var result = await gateway.ExecuteAsync(
                hostActionEntry,
                invocation.HostActionContext,
                action,
                ct);
            return new CliResult(
                true,
                [new CliOutput("stdout", result.Content)]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException exception)
        {
            return Failure("module_dev_cli_invalid", exception.Message);
        }
        catch (InvalidOperationException)
        {
            return Failure("module_dev_cli_failed", "The ModuleDev command failed.");
        }
    }

    private static ModuleDevAction Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
            throw new ArgumentException("A ModuleDev subcommand is required.");

        return arguments[0].ToLowerInvariant() switch
        {
            "scaffold" when arguments.Count >= 4 => new ModuleDevAction(
                ModuleDevOperation.ScaffoldModule,
                JsonSerializer.SerializeToElement(new
                {
                    module_id = arguments[1],
                    display_name = arguments[2],
                    tool_prefix = arguments[3],
                    description = arguments.Count > 4
                        ? string.Join(' ', arguments.Skip(4))
                        : null,
                })),
            "build" when arguments.Count >= 2 => new ModuleDevAction(
                ModuleDevOperation.BuildModule,
                JsonSerializer.SerializeToElement(new
                {
                    module_id = arguments[1],
                    configuration = arguments.Contains("--release", StringComparer.OrdinalIgnoreCase)
                        ? "Release"
                        : "Debug",
                })),
            "load" when arguments.Count == 2 => ModuleAction(
                ModuleDevOperation.LoadModule,
                arguments[1]),
            "unload" when arguments.Count == 2 => ModuleAction(
                ModuleDevOperation.UnloadModule,
                arguments[1]),
            "reload" when arguments.Count == 2 => ModuleAction(
                ModuleDevOperation.ReloadModule,
                arguments[1]),
            "inspect" when arguments.Count == 2 => new ModuleDevAction(
                ModuleDevOperation.InspectProcess,
                JsonSerializer.SerializeToElement(new { target = arguments[1] })),
            "env" when arguments.Count == 1 => EmptyAction(
                ModuleDevOperation.EnumerateDevEnvironment),
            "list" when arguments.Count == 1 => EmptyAction(
                ModuleDevOperation.ListWorkspaces),
            _ => throw new ArgumentException("The ModuleDev subcommand or arguments are invalid."),
        };
    }

    private static ModuleDevAction ModuleAction(ModuleDevOperation operation, string SourceId) =>
        new(operation, JsonSerializer.SerializeToElement(new { module_id = SourceId }));

    private static ModuleDevAction EmptyAction(ModuleDevOperation operation) =>
        new(operation, JsonSerializer.SerializeToElement(new { }));

    private static CliResult Failure(string code, string message) =>
        new(
            false,
            [new CliOutput("stderr", message)],
            new ExecutionError(code, message));
}

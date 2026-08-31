using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.ModuleDev.Services;

/// <summary>Runs read-only ModuleDev operations.</summary>
internal sealed class ModuleDevReadTerminal(ModuleDevOperations operations)
    : IHostActionEntryTerminal<ModuleDevAction, ModuleDevActionResult>
{
    public Guid TerminalId => ModuleDevContracts.ReadTerminalId;

    public ValueTask<ModuleDevActionResult> InvokeAsync(
        ActionContext<ModuleDevAction> context,
        CancellationToken ct)
    {
        if (!ModuleDevContracts.IsRead(context.Action.Operation))
            throw new ArgumentException("The requested operation is not read-only.");

        return operations.ExecuteAsync(context, ct);
    }
}

/// <summary>Runs irreversible ModuleDev operations.</summary>
internal sealed class ModuleDevMutationTerminal(ModuleDevOperations operations)
    : IHostActionEntryTerminal<ModuleDevAction, ModuleDevActionResult>
{
    public Guid TerminalId => ModuleDevContracts.MutationTerminalId;

    public ValueTask<ModuleDevActionResult> InvokeAsync(
        ActionContext<ModuleDevAction> context,
        CancellationToken ct)
    {
        if (ModuleDevContracts.IsRead(context.Action.Operation))
            throw new ArgumentException("The requested operation is not a mutation.");

        return operations.ExecuteAsync(context, ct);
    }
}

/// <summary>Routes one authenticated ingress through the ModuleDev action boundary.</summary>
internal sealed class ModuleDevActionGateway(
    ModuleDevReadTerminal readTerminal,
    ModuleDevMutationTerminal mutationTerminal)
{
    public async ValueTask<ModuleDevActionResult> ExecuteAsync(
        IHostActionEntry hostActionEntry,
        HostActionEntryRequestContext hostContext,
        ModuleDevAction action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hostActionEntry);
        ArgumentNullException.ThrowIfNull(hostContext);
        ArgumentNullException.ThrowIfNull(action);
        if (!Enum.IsDefined(action.Operation) || action.Parameters.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The ModuleDev action is not well formed.");

        var read = ModuleDevContracts.IsRead(action.Operation);
        var descriptor = read
            ? ModuleDevContracts.ReadDescriptor
            : ModuleDevContracts.MutationDescriptor;
        IActionOutcome<ModuleDevActionResult> outcome = read
            ? await hostActionEntry.InvokeAsync(
                new HostActionEntryRequest<ModuleDevAction, ModuleDevActionResult>(
                    descriptor,
                    action,
                    hostContext),
                readTerminal,
                ct)
            : await hostActionEntry.InvokeAsync(
                new HostActionEntryRequest<ModuleDevAction, ModuleDevActionResult>(
                    descriptor,
                    action,
                    hostContext),
                mutationTerminal,
                ct);

        if (outcome.Kind != ActionOutcomeKind.Completed || outcome.Result is null)
        {
            throw new InvalidOperationException(
                outcome.Error?.Message ?? "The ModuleDev action did not complete.");
        }

        return outcome.Result;
    }
}

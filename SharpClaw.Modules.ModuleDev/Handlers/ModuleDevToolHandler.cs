using SharpClaw.Contracts.Kernel;
using SharpClaw.Modules.ModuleDev.Services;

namespace SharpClaw.Modules.ModuleDev.Handlers;

/// <summary>Routes every ModuleDev Tool through one typed action boundary.</summary>
internal sealed class ModuleDevToolHandler(
    IHostActionEntry hostActionEntry,
    ModuleDevActionGateway gateway) : IToolHandler
{
    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var operation = ModuleDevContracts.OperationForTool(invocation.ToolName);
        var result = await gateway.ExecuteAsync(
            hostActionEntry,
            invocation.HostActionContext,
            new ModuleDevAction(operation, invocation.Arguments, invocation.ConversationId),
            ct);
        return ToolResult.Text(result.Content);
    }
}

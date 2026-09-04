using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.ModuleDev.Handlers;
using SharpClaw.Modules.ModuleDev.Services;

namespace SharpClaw.Modules.ModuleDev;

/// <summary>Provides neutral module development operations.</summary>
public sealed class ModuleDevModule : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new(
        ModuleDevContracts.SourceId,
        "Module Development Kit",
        "mdk");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ModuleWorkspaceService>();
        services.AddSingleton<ModuleBuildService>();
        services.AddSingleton<ModuleScaffoldService>();
        services.AddSingleton<SharpClawSdkReferenceService>();
        services.AddSingleton<DevEnvironmentService>();
        services.AddSingleton<ProcessInspectionService>();
        services.AddSingleton<ComTypeLibInspector>();
        services.AddSingleton<ModuleDevOperations>();
        services.AddSingleton<ModuleDevReadTerminal>();
        services.AddSingleton<ModuleDevMutationTerminal>();
        services.AddSingleton<ModuleDevActionGateway>();
        services.AddSingleton<ModuleDevToolHandler>();
        services.AddSingleton<ModuleDevCliHandler>();
        services.AddSingleton<ModuleDevEndpointHandler>();

        services.AddAction(ModuleDevContracts.ReadDescriptor)
            .UseTerminal<ModuleDevReadTerminal>(ModuleDevContracts.ReadTerminalId);
        services.AddAction(ModuleDevContracts.MutationDescriptor)
            .UseTerminal<ModuleDevMutationTerminal>(ModuleDevContracts.MutationTerminalId);

        foreach (var descriptor in ModuleDevContracts.ToolDescriptors)
            services.AddTool<ModuleDevToolHandler>(descriptor);

        services.AddCliCommand<ModuleDevCliHandler>(ModuleDevCliHandler.Descriptor);
        foreach (var route in ModuleDevEndpointHandler.Routes)
            services.AddHttpEndpoint<ModuleDevEndpointHandler>(route);
    }

    public ValueTask StartAsync(ServiceStartContext context, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) =>
        ValueTask.CompletedTask;
}

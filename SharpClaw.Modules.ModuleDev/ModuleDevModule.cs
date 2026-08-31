using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;
using SharpClaw.Modules.ModuleDev.Handlers;
using SharpClaw.Modules.ModuleDev.Services;

namespace SharpClaw.Modules.ModuleDev;

/// <summary>Provides neutral module development operations.</summary>
public sealed class ModuleDevModule : ISharpClawModule, ISharpClawApplicationModule
{
    public ModuleIdentity Identity { get; } = new(
        ModuleDevContracts.ModuleId,
        "Module Development Kit",
        "mdk");

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddSingleton<ModuleWorkspaceService>();
        module.Services.AddSingleton<ModuleBuildService>();
        module.Services.AddSingleton<ModuleScaffoldService>();
        module.Services.AddSingleton<SharpClawSdkReferenceService>();
        module.Services.AddSingleton<DevEnvironmentService>();
        module.Services.AddSingleton<ProcessInspectionService>();
        module.Services.AddSingleton<ComTypeLibInspector>();
        module.Services.AddSingleton<ModuleDevOperations>();
        module.Services.AddSingleton<ModuleDevReadTerminal>();
        module.Services.AddSingleton<ModuleDevMutationTerminal>();
        module.Services.AddSingleton<ModuleDevActionGateway>();
        module.Services.AddSingleton<ModuleDevToolHandler>();
        module.Services.AddSingleton<ModuleDevCliHandler>();
        module.Services.AddSingleton<ModuleDevEndpointHandler>();

        module.Actions.Add(ModuleDevContracts.ReadDescriptor);
        module.AddActionEntry<ModuleDevAction, ModuleDevActionResult, ModuleDevReadTerminal>(
            ModuleDevContracts.ReadDescriptor,
            ModuleDevContracts.ReadTerminalId);
        module.Actions.Add(ModuleDevContracts.MutationDescriptor);
        module.AddActionEntry<ModuleDevAction, ModuleDevActionResult, ModuleDevMutationTerminal>(
            ModuleDevContracts.MutationDescriptor,
            ModuleDevContracts.MutationTerminalId);

        foreach (var descriptor in ModuleDevContracts.ToolDescriptors)
            module.Tools.Add<ModuleDevToolHandler>(descriptor);
    }

    public void ConfigureApplication(ISharpClawApplicationBuilder application)
    {
        application.Cli.Add<ModuleDevCliHandler>(ModuleDevCliHandler.Descriptor);
        foreach (var route in ModuleDevEndpointHandler.Routes)
            application.Endpoints.AddHttp<ModuleDevEndpointHandler>(route);
    }

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) =>
        ValueTask.CompletedTask;
}

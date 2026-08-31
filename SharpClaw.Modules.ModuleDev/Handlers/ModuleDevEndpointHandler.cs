using System.Text.Json;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.ModuleDev.Services;

namespace SharpClaw.Modules.ModuleDev.Handlers;

/// <summary>Runs all ModuleDev HTTP routes through typed action authority.</summary>
internal sealed class ModuleDevEndpointHandler(ModuleDevActionGateway gateway)
    : IModuleHttpEndpointHandler
{
    private static IReadOnlyList<RouteDefinition> Definitions { get; } =
    [
        Route("module-dev.scaffold", "/modules/dev/scaffold", "POST", ModuleDevOperation.ScaffoldModule),
        Route("module-dev.files.list", "/modules/dev/{moduleId}/files", "GET", ModuleDevOperation.ListFiles),
        Route("module-dev.files.read", "/modules/dev/{moduleId}/files/{**path}", "GET", ModuleDevOperation.ReadFile),
        Route("module-dev.files.write", "/modules/dev/{moduleId}/files/{**path}", "PUT", ModuleDevOperation.WriteFile),
        Route("module-dev.build", "/modules/dev/{moduleId}/build", "POST", ModuleDevOperation.BuildModule),
        Route("module-dev.load", "/modules/dev/{moduleId}/load", "POST", ModuleDevOperation.LoadModule),
        Route("module-dev.unload", "/modules/dev/{moduleId}/load", "DELETE", ModuleDevOperation.UnloadModule),
        Route("module-dev.reload", "/modules/dev/{moduleId}/reload", "POST", ModuleDevOperation.ReloadModule),
        Route("module-dev.inspect", "/modules/dev/inspect/{target}", "GET", ModuleDevOperation.InspectProcess),
        Route("module-dev.com", "/modules/dev/com/{**typelibPath}", "GET", ModuleDevOperation.DiscoverComInterfaces),
        Route("module-dev.environment", "/modules/dev/env", "GET", ModuleDevOperation.EnumerateDevEnvironment),
    ];

    public static IReadOnlyList<ModuleEndpointRouteDescriptor> Routes { get; } =
        Definitions.Select(definition => definition.Descriptor).ToArray();

    public async ValueTask<ModuleHttpEndpointResponse> InvokeAsync(
        HostEndpointRouteRequest request,
        IHostActionEntry hostActionEntry,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = Definitions.SingleOrDefault(candidate =>
            candidate.Descriptor.ToRouteIdentity().Equals(request.Route));
        if (route is null)
            return Error(404, "module_dev_route_not_found");

        try
        {
            var parameters = CreateParameters(route.Operation, request);
            var result = await gateway.ExecuteAsync(
                hostActionEntry,
                request.Invocation.HostActionContext,
                new ModuleDevAction(route.Operation, parameters),
                ct);
            return ModuleHttpEndpointResponse.Json(200, ParseResult(result.Content));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return Error(404, "module_dev_file_not_found");
        }
        catch (UnauthorizedAccessException)
        {
            return Error(403, "module_dev_forbidden");
        }
        catch (JsonException)
        {
            return Error(400, "module_dev_invalid_json");
        }
        catch (ArgumentException)
        {
            return Error(400, "module_dev_invalid_request");
        }
        catch (InvalidOperationException)
        {
            return Error(500, "module_dev_operation_failed");
        }
    }

    private static JsonElement CreateParameters(
        ModuleDevOperation operation,
        HostEndpointRouteRequest request)
    {
        var moduleId = RouteValue(request, "moduleId");
        var path = RouteValue(request, "path");
        return operation switch
        {
            ModuleDevOperation.ScaffoldModule => ReadBodyObject(request.Body),
            ModuleDevOperation.ListFiles => JsonSerializer.SerializeToElement(new
            {
                module_id = moduleId,
                include_pattern = QueryValue(request, "pattern"),
            }),
            ModuleDevOperation.ReadFile => JsonSerializer.SerializeToElement(new
            {
                module_id = moduleId,
                relative_path = path,
                max_lines = QueryInt(request, "maxLines"),
            }),
            ModuleDevOperation.WriteFile => JsonSerializer.SerializeToElement(new
            {
                module_id = moduleId,
                relative_path = path,
                content = RequiredBodyString(request.Body, "content"),
            }),
            ModuleDevOperation.BuildModule => JsonSerializer.SerializeToElement(new
            {
                module_id = moduleId,
                configuration = OptionalBodyString(request.Body, "configuration") ?? "Debug",
            }),
            ModuleDevOperation.LoadModule or
            ModuleDevOperation.UnloadModule or
            ModuleDevOperation.ReloadModule => JsonSerializer.SerializeToElement(new
            {
                module_id = moduleId,
            }),
            ModuleDevOperation.InspectProcess => JsonSerializer.SerializeToElement(new
            {
                target = RouteValue(request, "target"),
                include = Split(QueryValue(request, "include")),
                export_filter = QueryValue(request, "exportFilter"),
            }),
            ModuleDevOperation.DiscoverComInterfaces => JsonSerializer.SerializeToElement(new
            {
                typelib_path = RouteValue(request, "typelibPath"),
                interface_filter = QueryValue(request, "interfaceFilter"),
                include_inherited = QueryBool(request, "includeInherited"),
            }),
            ModuleDevOperation.EnumerateDevEnvironment =>
                JsonSerializer.SerializeToElement(new { }),
            _ => throw new ArgumentException("The endpoint operation is not supported."),
        };
    }

    private static JsonElement ParseResult(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(content);
        }
    }

    private static JsonElement ReadBodyObject(byte[] body)
    {
        if (body is null || body.Length == 0)
            throw new JsonException("The request body is empty.");
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The request body must contain one object.");
        return document.RootElement.Clone();
    }

    private static string RequiredBodyString(byte[] body, string name) =>
        OptionalBodyString(body, name)
        ?? throw new ArgumentException($"{name} is required.");

    private static string? OptionalBodyString(byte[] body, string name)
    {
        if (body is null || body.Length == 0)
            return null;
        var value = ReadBodyObject(body);
        return value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static string RouteValue(HostEndpointRouteRequest request, string name)
    {
        if (!request.RouteValues.TryGetValue(name, out var values) ||
            values.Length != 1 ||
            string.IsNullOrWhiteSpace(values[0]))
        {
            throw new ArgumentException($"Route value '{name}' is required.");
        }

        return values[0];
    }

    private static string? QueryValue(HostEndpointRouteRequest request, string name) =>
        request.Query.TryGetValue(name, out var values) && values.Length == 1
            ? values[0]
            : null;

    private static int? QueryInt(HostEndpointRouteRequest request, string name) =>
        QueryValue(request, name) is { } raw && int.TryParse(raw, out var value)
            ? value
            : null;

    private static bool? QueryBool(HostEndpointRouteRequest request, string name) =>
        QueryValue(request, name) is { } raw && bool.TryParse(raw, out var value)
            ? value
            : null;

    private static string[]? Split(string? value) =>
        value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static RouteDefinition Route(
        string id,
        string path,
        string method,
        ModuleDevOperation operation) =>
        new(
            new ModuleEndpointRouteDescriptor(id, path, method, HostEndpointTransport.Http),
            operation);

    private static ModuleHttpEndpointResponse Error(int statusCode, string code) =>
        ModuleHttpEndpointResponse.Json(
            statusCode,
            JsonSerializer.SerializeToElement(new { error = code }));

    private sealed record RouteDefinition(
        ModuleEndpointRouteDescriptor Descriptor,
        ModuleDevOperation Operation);
}

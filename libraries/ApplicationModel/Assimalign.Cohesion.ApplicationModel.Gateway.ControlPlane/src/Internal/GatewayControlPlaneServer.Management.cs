using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

// The management API: /cohesion/v2/resources/{name}/... over the gateway's IResourceManagementProvider. The server
// authenticates the caller as on every route, checks the caller's management grants for the operation and its target,
// and maps the provider's result to HTTP; the provider owns every record and instance decision.
internal sealed partial class GatewayControlPlaneServer
{
    private IResourceManagementProvider? _management;

    private async Task HandleGetConfigurationAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken)
    {
        if (await AuthorizeManagementAsync(context, ResourceManagementOperations.ReadConfiguration, null, cancellationToken)
                .ConfigureAwait(false) is not { } access)
        {
            return;
        }

        ResourceManagementResult result = await access.Provider
            .GetConfigurationAsync(_application, GetResource(routeValues), cancellationToken)
            .ConfigureAwait(false);
        await WriteManagementResultAsync(context, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleGetConfigurationStatusAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken)
    {
        if (await AuthorizeManagementAsync(context, ResourceManagementOperations.ReadConfiguration, null, cancellationToken)
                .ConfigureAwait(false) is not { } access)
        {
            return;
        }

        ResourceManagementResult result = await access.Provider
            .GetConfigurationStatusAsync(_application, GetResource(routeValues), cancellationToken)
            .ConfigureAwait(false);
        await WriteManagementResultAsync(context, result, cancellationToken).ConfigureAwait(false);
    }

    private Task HandleMergeOverrideAsync(IHttpContext context, IReadOnlyDictionary<string, string> routeValues, CancellationToken cancellationToken) =>
        ChangeOverrideAsync(context, routeValues, ResourceOverrideChangeKind.Merge, cancellationToken);

    private Task HandleReplaceOverrideAsync(IHttpContext context, IReadOnlyDictionary<string, string> routeValues, CancellationToken cancellationToken) =>
        ChangeOverrideAsync(context, routeValues, ResourceOverrideChangeKind.Replace, cancellationToken);

    private Task HandleRemoveOverrideAsync(IHttpContext context, IReadOnlyDictionary<string, string> routeValues, CancellationToken cancellationToken) =>
        ChangeOverrideAsync(context, routeValues, ResourceOverrideChangeKind.Remove, cancellationToken);

    private async Task ChangeOverrideAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        ResourceOverrideChangeKind kind,
        CancellationToken cancellationToken)
    {
        string section = GetRouteText(routeValues, "section");
        if (await AuthorizeManagementAsync(context, ResourceManagementOperations.WriteConfiguration, section, cancellationToken)
                .ConfigureAwait(false) is not { } access)
        {
            return;
        }

        if (!ResourceSectionPath.IsValid(section, out _))
        {
            await WriteManagementResultAsync(
                    context,
                    ResourceManagementResult.Refuse(ResourceManagementStatus.UnknownSection, $"'{section}' is not a configuration section path."),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        byte[] patch = kind == ResourceOverrideChangeKind.Remove
            ? Array.Empty<byte>()
            : await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        ResourceManagementResult result = await access.Provider
            .ChangeOverrideAsync(_application, GetResource(routeValues), section, kind, patch, GetExpectedVersion(context), access.Caller, cancellationToken)
            .ConfigureAwait(false);
        await WriteManagementResultAsync(context, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleReplaceClaimsAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken)
    {
        if (await AuthorizeManagementAsync(context, ResourceManagementOperations.WriteClaims, null, cancellationToken)
                .ConfigureAwait(false) is not { } access)
        {
            return;
        }

        // A claim belongs to the application whose code declares it, so a caller writes only its own application's.
        string owner = GetRouteText(routeValues, "owner");
        if (!ApplicationName.IsValid(owner, out _) || access.Caller.Application is not ApplicationName caller || caller.ToString() != owner)
        {
            await WriteManagementErrorAsync(
                    context,
                    HttpStatusCode.Forbidden,
                    "forbidden",
                    $"Claims belong to the application that declares them; the caller cannot write the claims of '{owner}'.",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        byte[] entries = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        ResourceManagementResult result = await access.Provider
            .ReplaceClaimsAsync(_application, GetResource(routeValues), owner, entries, GetExpectedVersion(context), access.Caller, cancellationToken)
            .ConfigureAwait(false);
        await WriteManagementResultAsync(context, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleInvokeActionAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken)
    {
        string action = GetRouteText(routeValues, "action");
        if (await AuthorizeManagementAsync(context, ResourceManagementOperations.InvokeActions, action, cancellationToken)
                .ConfigureAwait(false) is not { } access)
        {
            return;
        }

        byte[] input = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        ResourceManagementResult result = await access.Provider
            .InvokeActionAsync(_application, GetResource(routeValues), action, input, access.Caller, cancellationToken)
            .ConfigureAwait(false);
        await WriteManagementResultAsync(context, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleGetInstancesAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken)
    {
        if (await AuthorizeManagementAsync(context, ResourceManagementOperations.ReadConfiguration, null, cancellationToken)
                .ConfigureAwait(false) is not { } access)
        {
            return;
        }

        ResourceManagementResult result = await access.Provider
            .GetInstanceInventoryAsync(_application, GetResource(routeValues), cancellationToken)
            .ConfigureAwait(false);
        await WriteManagementResultAsync(context, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleGetPlatformSurfaceAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken)
    {
        string surface = GetRouteText(routeValues, "platform") + "/" + GetRouteText(routeValues, "extension");
        if (await AuthorizeManagementAsync(context, ResourceManagementOperations.ReadPlatform, surface, cancellationToken)
                .ConfigureAwait(false) is not { } access)
        {
            return;
        }

        ResourceManagementResult result = ResourceExtensionKey.IsValid(surface, out _)
            ? await access.Provider.GetPlatformSurfaceAsync(_application, GetResource(routeValues), surface, cancellationToken).ConfigureAwait(false)
            : ResourceManagementResult.Refuse(ResourceManagementStatus.UnknownPlatformSurface, $"'{surface}' is not a platform surface key.");
        await WriteManagementResultAsync(context, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleChangePlatformSurfaceAsync(
        IHttpContext context,
        IReadOnlyDictionary<string, string> routeValues,
        CancellationToken cancellationToken)
    {
        string surface = GetRouteText(routeValues, "platform") + "/" + GetRouteText(routeValues, "extension");
        if (await AuthorizeManagementAsync(context, ResourceManagementOperations.WritePlatform, surface, cancellationToken)
                .ConfigureAwait(false) is not { } access)
        {
            return;
        }

        byte[] patch = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
        ResourceManagementResult result = ResourceExtensionKey.IsValid(surface, out _)
            ? await access.Provider
                .ChangePlatformSurfaceAsync(_application, GetResource(routeValues), surface, patch, GetExpectedVersion(context), access.Caller, cancellationToken)
                .ConfigureAwait(false)
            : ResourceManagementResult.Refuse(ResourceManagementStatus.UnknownPlatformSurface, $"'{surface}' is not a platform surface key.");
        await WriteManagementResultAsync(context, result, cancellationToken).ConfigureAwait(false);
    }

    // Authenticates like every route, then requires a management grant for the operation and its target. An empty
    // grant list grants nothing; the gateway's own issuer grants everything.
    private async ValueTask<(ApplicationCaller Caller, IResourceManagementProvider Provider)?> AuthorizeManagementAsync(
        IHttpContext context,
        string operation,
        string? target,
        CancellationToken cancellationToken)
    {
        ApplicationCaller? caller = await AuthorizeAsync(context, requireCommandAccess: false, cancellationToken).ConfigureAwait(false);
        if (caller is null)
        {
            return null;
        }

        if (!ResourceManagementOperations.IsGranted(caller.AllowedManagementOperations, operation, target))
        {
            context.Response.Headers[HttpHeaderKey.WWWAuthenticate] = "Bearer error=\"insufficient_scope\"";
            await WriteManagementErrorAsync(
                    context,
                    HttpStatusCode.Forbidden,
                    "forbidden",
                    target is null
                        ? $"The caller holds no '{operation}' management grant."
                        : $"The caller holds no '{operation}' management grant for '{target}'.",
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        if (_management is not IResourceManagementProvider provider)
        {
            await WriteManagementErrorAsync(
                    context,
                    HttpStatusCode.NotImplemented,
                    ResourceManagementResult.GetErrorCode(ResourceManagementStatus.RecordsUnsupported),
                    "This gateway serves no management API.",
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        return (caller, provider);
    }

    private static async Task WriteManagementResultAsync(IHttpContext context, ResourceManagementResult result, CancellationToken cancellationToken)
    {
        PrepareJsonResponse(context, result.Status switch
        {
            ResourceManagementStatus.Ok => HttpStatusCode.Ok,
            ResourceManagementStatus.InvalidConfiguration or ResourceManagementStatus.InvalidInput => HttpStatusCode.BadRequest,
            ResourceManagementStatus.UnknownResource or
            ResourceManagementStatus.UnknownSection or
            ResourceManagementStatus.UnknownAction or
            ResourceManagementStatus.UnknownPlatformSurface => HttpStatusCode.NotFound,
            ResourceManagementStatus.DeploymentOnly or
            ResourceManagementStatus.NotOverridable or
            ResourceManagementStatus.NotClaimable or
            ResourceManagementStatus.ClaimConflict => HttpStatusCode.Conflict,
            ResourceManagementStatus.VersionMoved => HttpStatusCode.PreconditionFailed,
            ResourceManagementStatus.RecordsUnsupported => HttpStatusCode.NotImplemented,
            _ => HttpStatusCode.ServiceUnavailable,
        });
        if (result.Version is not null)
        {
            context.Response.Headers[HttpHeaderKey.ETag] = "\"" + result.Version + "\"";
        }

        if (context.Request.Method != CohesionHttpMethod.Head)
        {
            await context.Response.Body.WriteAsync(result.Document, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteManagementErrorAsync(
        IHttpContext context,
        HttpStatusCode statusCode,
        string error,
        string detail,
        CancellationToken cancellationToken)
    {
        PrepareJsonResponse(context, statusCode);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("error", error);
            writer.WriteString("detail", detail);
            writer.WriteEndObject();
        }

        await context.Response.Body.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    // The listener bounds the body at MaximumCommandBytes, which is also the deployment record's layer limit.
    private static async ValueTask<byte[]> ReadBodyAsync(IHttpContext context, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    // If-Match names the record version the caller read; '*' or no header writes unconditionally.
    private static string? GetExpectedVersion(IHttpContext context)
    {
        string? value = context.Request.Headers.GetValue(HttpHeaderKey.IfMatch)?.Trim();
        if (string.IsNullOrEmpty(value) || value == "*")
        {
            return null;
        }

        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
    }

    private static ResourceName GetResource(IReadOnlyDictionary<string, string> routeValues) => GetRouteText(routeValues, "name");

    // Route values are raw segments; a client may percent-encode the colons of a section path.
    private static string GetRouteText(IReadOnlyDictionary<string, string> routeValues, string key) =>
        routeValues.TryGetValue(key, out string? value) ? Uri.UnescapeDataString(value) : string.Empty;
}

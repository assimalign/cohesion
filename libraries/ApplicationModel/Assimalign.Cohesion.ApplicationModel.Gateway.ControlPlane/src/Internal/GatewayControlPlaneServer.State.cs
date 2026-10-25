using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

internal sealed partial class GatewayControlPlaneServer
{
    private async Task HandleResourceStateManagementAsync(IHttpContext context, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store";
        ApplicationCaller? caller = await AuthorizeAsync(context, requireCommandAccess: false, cancellationToken).ConfigureAwait(false);
        if (caller is null) { return; }
        if (_managementGateway is null) { context.Response.StatusCode = HttpStatusCode.ServiceUnavailable; return; }
        if (!TryGetResourceRoute(values, out IApplicationResourceDescriptor? descriptor, out _) || context.Request.Query.Count != 0)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }
        ResourceName resource = descriptor!.Resource.Name;
        bool clearOverride = context.Request.Method == CohesionHttpMethod.Delete;
        bool write = context.Request.Method == CohesionHttpMethod.Put || clearOverride;
        try
        {
            if (context.Request.Path.Value.EndsWith("/state-contracts", StringComparison.Ordinal))
            {
                if (!AuthorizeManagement(context, caller, resource.Value + "/state-contracts/read")) { return; }
                JsonElement runtime = _managementGateway.GetRuntimeManifest(_application, resource);
                using var catalog = new MemoryStream();
                using (var writer = new Utf8JsonWriter(catalog))
                {
                    writer.WriteStartObject();
                    foreach (string property in new[] { "schema", "management", "states" })
                    {
                        writer.WritePropertyName(property);
                        runtime.GetProperty(property).WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                await WriteStateJsonAsync(context, catalog.ToArray(), cancellationToken).ConfigureAwait(false);
                return;
            }
            if (context.Request.Path.Value.EndsWith("/instances", StringComparison.Ordinal))
            {
                if (!AuthorizeManagement(context, caller, resource.Value + "/instances/read")) { return; }
                ResourceInstanceInventory inventory = await _managementGateway.GetInstancesAsync(_application, resource, cancellationToken).ConfigureAwait(false);
                await WriteStateJsonAsync(context, JsonSerializer.SerializeToUtf8Bytes(inventory, ResourceManagementWireJsonContext.Default.ResourceInstanceInventory), cancellationToken).ConfigureAwait(false);
                return;
            }
            if (values.TryGetValue("operationId", out string? operationText))
            {
                if (!Guid.TryParse(operationText, out Guid id)) { context.Response.StatusCode = HttpStatusCode.BadRequest; return; }
                ResourceStateOperation? operation = await _managementGateway.GetStateOperationAsync(_application, resource, id, cancellationToken).ConfigureAwait(false);
                if (operation is null) { context.Response.StatusCode = HttpStatusCode.NotFound; return; }
                if (!AuthorizeManagement(context, caller, resource.Value + "/state/" + operation.Address.State.Value + "/read")) { return; }
                await WriteStateJsonAsync(context, JsonSerializer.SerializeToUtf8Bytes(operation, ResourceManagementWireJsonContext.Default.ResourceStateOperation), cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!values.TryGetValue("state", out string? stateText) || !ResourceStateName.TryCreate(stateText, out ResourceStateName name) ||
                !values.TryGetValue("key", out string? keyText) || !ResourceStateKey.TryCreate(Uri.UnescapeDataString(keyText), out ResourceStateKey key))
            {
                context.Response.StatusCode = HttpStatusCode.BadRequest;
                return;
            }
            if (!AuthorizeManagement(context, caller, resource.Value + "/state/" + name.Value + (write ? "/write" : "/read"))) { return; }
            var address = new ResourceStateAddress(_application, resource, name, key);
            ResourceStateOperation? result;
            if (write)
            {
                string? match = context.Request.Headers.GetValue("If-Match");
                string? absent = context.Request.Headers.GetValue("If-None-Match");
                long? expected = null;
                if (match is not null)
                {
                    if (absent is not null || match.Length < 3 || match[0] != '"' || match[^1] != '"' ||
                        !long.TryParse(match[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out long revision) || revision <= 0)
                    {
                        context.Response.StatusCode = HttpStatusCode.BadRequest;
                        return;
                    }
                    expected = revision;
                }
                else if (absent != "*" || clearOverride) { context.Response.StatusCode = (HttpStatusCode)428; return; }
                if (!Guid.TryParse(context.Request.Headers.GetValue("Idempotency-Key"), out Guid requestId) || requestId == Guid.Empty)
                {
                    context.Response.StatusCode = HttpStatusCode.BadRequest;
                    return;
                }
                if (!clearOverride && (!MediaTypeHeaderValue.TryParse(context.Request.Headers.GetValue(HttpHeaderKey.ContentType), out MediaTypeHeaderValue? media) ||
                    !string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                    )
                {
                    context.Response.StatusCode = HttpStatusCode.UnsupportedMediaType;
                    return;
                }
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[8192];
                while (true)
                {
                    int read = await context.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (read == 0) { break; }
                    if (buffer.Length + read > MaximumCommandBytes) { context.Response.StatusCode = HttpStatusCode.RequestEntityTooLarge; return; }
                    buffer.Write(chunk, 0, read);
                }
                using JsonDocument empty = JsonDocument.Parse("null");
                ResourceDesiredValue? desired = clearOverride ? new ResourceDesiredValue(false, empty.RootElement.Clone()) :
                    JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), ControlPlaneJsonContext.Default.ResourceDesiredValue);
                if (desired is null) { context.Response.StatusCode = HttpStatusCode.BadRequest; return; }
                string principal = (caller.Application?.Value ?? string.Empty) + "\0" + caller.Kind + "\0" + caller.Subject;
                result = await _managementGateway.SetStateAsync(new ResourceStateChange(requestId, address, expected, desired, clearOverride), principal, cancellationToken).ConfigureAwait(false);
                context.Response.StatusCode = HttpStatusCode.Accepted;
                context.Response.Headers[HttpHeaderKey.Location] = "/cohesion/v2/resources/" + resource.Value + "/operations/" + result.Id.ToString("D");
            }
            else { result = await _managementGateway.GetStateAsync(address, cancellationToken).ConfigureAwait(false); }
            if (result is null) { context.Response.StatusCode = HttpStatusCode.NotFound; return; }
            context.Response.Headers[HttpHeaderKey.ETag] = "\"" + result.Revision.ToString(CultureInfo.InvariantCulture) + "\"";
            await WriteStateJsonAsync(context, JsonSerializer.SerializeToUtf8Bytes(result, ResourceManagementWireJsonContext.Default.ResourceStateOperation), cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException) { context.Response.StatusCode = HttpStatusCode.NotFound; }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException) { context.Response.StatusCode = HttpStatusCode.BadRequest; }
        catch (InvalidOperationException) { context.Response.StatusCode = HttpStatusCode.Conflict; }
    }

    private static bool AuthorizeManagement(IHttpContext context, ApplicationCaller caller, string operation)
    {
        if (!Enum.IsDefined(caller.Kind) ||
            (operation.EndsWith("/write", StringComparison.Ordinal) && caller.Kind is not (ApplicationCallerKind.Peer or ApplicationCallerKind.Operator)))
        {
            context.Response.StatusCode = HttpStatusCode.Forbidden;
            return false;
        }
        if (caller.AllowedManagementOperations.Contains(operation, StringComparer.Ordinal) || caller.AllowedManagementOperations.Contains("*", StringComparer.Ordinal)) { return true; }
        context.Response.StatusCode = HttpStatusCode.Forbidden;
        return false;
    }

    private static async Task WriteStateJsonAsync(IHttpContext context, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        context.Response.Headers[HttpHeaderKey.ContentType] = "application/json; charset=utf-8";
        if (context.Request.Method != CohesionHttpMethod.Head) { await context.Response.Body.WriteAsync(content, cancellationToken).ConfigureAwait(false); }
    }
}

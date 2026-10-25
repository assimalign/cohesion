using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

[global::System.Diagnostics.DebuggerDisplay("ResourceStateProtocolClient")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ResourceStateProtocolClient : IResourceStateProtocolClient
{
    private readonly Func<ResourceStateAddress, ResourceManagementTarget, CancellationToken, ValueTask<HttpClient>> _client;

    public ResourceStateProtocolClient(Func<ResourceStateAddress, ResourceManagementTarget, CancellationToken, ValueTask<HttpClient>> client)
    {
        _client = client;
    }

    public async ValueTask<ResourceRuntimeDescription> DescribeAsync(ResourceManagementTarget target, ApplicationName application, ResourceName resource, CancellationToken cancellationToken = default)
    {
        using HttpClient client = await _client(new ResourceStateAddress(application, resource, "cohesion.bootstrap", "admission"), target, cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage response = await client.GetAsync(Append(RequestAddress(target), "runtime"), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        Guid process = document.RootElement.GetProperty("processStartId").GetGuid();
        if (process == Guid.Empty || (target.VerifyHostIdentity &&
            (!document.RootElement.TryGetProperty("platformInstance", out JsonElement host) || host.GetString() != target.HostId)))
        {
            throw new InvalidOperationException(ErrorMessages.ManagementResourceUnavailable);
        }
        return new ResourceRuntimeDescription(process, document.RootElement.GetProperty("manifest").Clone());
    }

    public async ValueTask<bool> AdmitAsync(ResourceManagementTarget target, ApplicationName application, ResourceName resource, Guid processStartId,
        IReadOnlyList<ResourceBootstrapState> states, CancellationToken cancellationToken = default)
    {
        using HttpClient client = await _client(new ResourceStateAddress(application, resource, "cohesion.bootstrap", "admission"), target, cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("processStartId", processStartId);
            writer.WriteStartArray("states");
            foreach (ResourceBootstrapState state in states)
            {
                writer.WriteStartObject();
                writer.WriteString("name", state.Name.Value);
                writer.WriteString("key", state.Key.Value);
                writer.WriteNumber("revision", state.Revision);
                writer.WriteString("valueDigest", state.ValueDigest);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await client.PostAsync(Append(RequestAddress(target), "admit"), content, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async ValueTask<ResourceRuntimeStateObservation> ReadAsync(ResourceManagementTarget target, ResourceStateAddress address, string contractDigest, CancellationToken cancellationToken = default)
    {
        using HttpClient client = await _client(address, target, cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage handshake = await client.GetAsync(Append(RequestAddress(target), "runtime"), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        handshake.EnsureSuccessStatusCode();
        using JsonDocument catalog = await ReadJsonAsync(handshake, cancellationToken).ConfigureAwait(false);
        JsonElement manifest = catalog.RootElement.GetProperty("manifest");
        if (manifest.GetProperty("schema").GetString() != "cohesion/resource-runtime/v1" ||
            manifest.GetProperty("management").GetProperty("protocol").GetString() != "cohesion/resource-state/v1")
        {
            throw new InvalidOperationException(ErrorMessages.ManagementStateUnsupported);
        }
        bool compatible = false;
        foreach (JsonElement definition in manifest.GetProperty("states").EnumerateArray())
        {
            if (ResourceStateWire.IsValidDefinition(definition, requireDigest: true) && definition.GetProperty("name").GetString() == address.State.Value &&
                definition.GetProperty("contractDigest").GetString() == contractDigest) { compatible = true; }
        }
        Guid processId = catalog.RootElement.GetProperty("processStartId").GetGuid();
        if (target.VerifyHostIdentity && (!catalog.RootElement.TryGetProperty("platformInstance", out JsonElement host) || host.GetString() != target.HostId))
        {
            throw new InvalidOperationException(ErrorMessages.ManagementResourceUnavailable);
        }
        if (!compatible || processId == Guid.Empty) { throw new InvalidOperationException(ErrorMessages.ManagementStateUnsupported); }
        using HttpResponseMessage response = await client.GetAsync(Append(RequestAddress(target), StatePath(address)), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (root.GetProperty("processStartId").GetGuid() != processId) { throw new InvalidOperationException(ErrorMessages.ManagementResourceUnavailable); }
        JsonElement value = root.GetProperty("value");
        return new ResourceRuntimeStateObservation(new ResourceDesiredValue(value.GetProperty("exists").GetBoolean(), value.GetProperty("value").Clone()),
            root.GetProperty("revision").GetInt64(), root.GetProperty("valueDigest").GetString()!, processId);
    }

    public async ValueTask<ResourceStateDeliveryResult> ApplyAsync(ResourceManagementTarget target, ResourceStateDelivery delivery, CancellationToken cancellationToken = default)
    {
        using HttpClient client = await _client(delivery.Address, target, cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("context");
            writer.WriteString("resourceIncarnation", delivery.Incarnation);
            writer.WriteString("targetProcess", delivery.ProcessStartId);
            writer.WriteNumber("revision", delivery.Revision);
            writer.WriteString("valueDigest", delivery.ValueDigest);
            writer.WriteNumber("fence", delivery.Fence);
            writer.WriteEndObject();
            writer.WritePropertyName("desired");
            JsonSerializer.Serialize(writer, delivery.Desired, ResourceManagementJsonContext.Default.ResourceDesiredValue);
            writer.WriteEndObject();
        }
        using var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await client.PutAsync(Append(RequestAddress(target), StatePath(delivery.Address)), content, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        string? status = root.TryGetProperty("status", out JsonElement statusElement) ? statusElement.GetString() : null;
        long revision = root.TryGetProperty("revision", out JsonElement revisionElement) ? revisionElement.GetInt64() : 0;
        return new ResourceStateDeliveryResult(revision, response.IsSuccessStatusCode && status is "Applied" or "AlreadyCurrent"
            ? null : root.TryGetProperty("errorCode", out JsonElement error) && error.ValueKind == JsonValueKind.String ? error.GetString() : "state-rejected");
    }

    private static Uri Append(Uri address, string suffix) => new(address.AbsoluteUri.TrimEnd('/') + "/" + suffix, UriKind.Absolute);
    private static Uri RequestAddress(ResourceManagementTarget target) => target.ServerName is null
        ? target.Address : new UriBuilder(target.Address) { Host = target.ServerName }.Uri;
    private static string StatePath(ResourceStateAddress address) => "state/" + Uri.EscapeDataString(address.State.Value!) + "/" + Uri.EscapeDataString(address.Key.Value!);

    private static async ValueTask<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)) { throw new InvalidDataException(ErrorMessages.ManagementStateInvalid); }
        if (response.Content.Headers.ContentLength > 1024 * 1024) { throw new InvalidDataException(ErrorMessages.ManagementStateInvalid); }
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int count = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) { break; }
            if (buffer.Length + count > 1024 * 1024) { throw new InvalidDataException(ErrorMessages.ManagementStateInvalid); }
            buffer.Write(chunk, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private sealed class DebugView
    {
        public DebugView(ResourceStateProtocolClient value) { }
    }
}

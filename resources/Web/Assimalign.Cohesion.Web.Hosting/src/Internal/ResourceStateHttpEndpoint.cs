using System;
using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

internal static class ResourceStateHttpEndpoint
{
    private const int MaximumBytes = 1024 * 1024;

    public static async ValueTask<bool> TryHandleAsync(IResourceControlPlane plane, ResourceContext resource, IHttpContext context, CancellationToken cancellationToken)
    {
        string path = context.Request.Path.Value;
        string rootPath = plane.RuntimeManagement?.Path.Value.TrimEnd('/') ?? "/cohesion/v1";
        if (path == rootPath + "/admit")
        {
            if (context.Request.Method != HttpMethod.Post) { context.Response.StatusCode = HttpStatusCode.MethodNotAllowed; context.Response.Headers[HttpHeaderKey.Allow] = "POST"; return true; }
            try
            {
                var admissionBuffer = new ArrayBufferWriter<byte>();
                while (true)
                {
                    int read = await context.Request.Body.ReadAsync(admissionBuffer.GetMemory(8192), cancellationToken).ConfigureAwait(false);
                    if (read == 0) { break; }
                    admissionBuffer.Advance(read);
                    if (admissionBuffer.WrittenCount > MaximumBytes) { context.Response.StatusCode = HttpStatusCode.RequestEntityTooLarge; return true; }
                }
                ResourceStateAdmission? admission = JsonSerializer.Deserialize(admissionBuffer.WrittenSpan, ResourceStateJsonContext.Default.ResourceStateAdmission);
                context.Response.StatusCode = admission is not null && await plane.AdmitStateAsync(admission, cancellationToken).ConfigureAwait(false)
                    ? HttpStatusCode.NoContent : HttpStatusCode.Conflict;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException) { context.Response.StatusCode = HttpStatusCode.BadRequest; }
            return true;
        }
        if (path == rootPath + "/runtime")
        {
            if (context.Request.Method != HttpMethod.Get && context.Request.Method != HttpMethod.Head)
            {
                context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
                context.Response.Headers[HttpHeaderKey.Allow] = "GET, HEAD";
                return true;
            }
            ResourceRuntimeManifest? manifest = plane.RuntimeManifest;
            if (manifest is null) { context.Response.StatusCode = HttpStatusCode.NotFound; return true; }
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("processStartId", plane.ProcessStartId);
                if (resource.TryGetEnvironmentValue(AppEnvironment.Variables.ResourceInstance, out string? instance)) { writer.WriteString("platformInstance", instance); }
                writer.WritePropertyName("manifest");
                JsonSerializer.Serialize(writer, manifest, ResourceStateJsonContext.Default.ResourceRuntimeManifest);
                writer.WriteEndObject();
            }
            await WriteAsync(context, buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            return true;
        }
        string prefix = rootPath + "/state/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) { return false; }
        string[] segments = path[prefix.Length..].Split('/');
        if (segments.Length != 2 || context.Request.Query.Count != 0 ||
            !ResourceStateName.TryCreate(segments[0], out ResourceStateName name) ||
            !ResourceStateKey.TryCreate(Uri.UnescapeDataString(segments[1]), out ResourceStateKey key))
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
            return true;
        }
        try
        {
            if (context.Request.Method == HttpMethod.Get || context.Request.Method == HttpMethod.Head)
            {
                ResourceStateObservation observed = await plane.ReadStateAsync(name, key, cancellationToken).ConfigureAwait(false);
                await WriteAsync(context, JsonSerializer.SerializeToUtf8Bytes(observed, ResourceStateJsonContext.Default.ResourceStateObservation), cancellationToken).ConfigureAwait(false);
                return true;
            }
            if (context.Request.Method != HttpMethod.Put)
            {
                context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
                context.Response.Headers[HttpHeaderKey.Allow] = "GET, HEAD, PUT";
                return true;
            }
            if (!MediaTypeHeaderValue.TryParse(context.Request.Headers.GetValue(HttpHeaderKey.ContentType), out MediaTypeHeaderValue? media) ||
                !string.Equals(media.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = HttpStatusCode.UnsupportedMediaType;
                return true;
            }
            var buffer = new ArrayBufferWriter<byte>();
            while (true)
            {
                Memory<byte> memory = buffer.GetMemory(8192);
                int read = await context.Request.Body.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
                if (read == 0) { break; }
                buffer.Advance(read);
                if (buffer.WrittenCount > MaximumBytes) { context.Response.StatusCode = HttpStatusCode.RequestEntityTooLarge; return true; }
            }
            ResourceStateApplyRequest? request = JsonSerializer.Deserialize(buffer.WrittenSpan, ResourceStateHttpJsonContext.Default.ResourceStateApplyRequest);
            if (request?.Context is null || request.Desired is null) { context.Response.StatusCode = HttpStatusCode.BadRequest; return true; }
            ResourceStateApplyResult result = await plane.ApplyStateAsync(request.Context, name, key, request.Desired, cancellationToken).ConfigureAwait(false);
            context.Response.StatusCode = result.Status is ResourceStateApplyStatus.Applied or ResourceStateApplyStatus.AlreadyCurrent
                ? HttpStatusCode.Ok : HttpStatusCode.Conflict;
            await WriteAsync(context, JsonSerializer.SerializeToUtf8Bytes(result, ResourceStateJsonContext.Default.ResourceStateApplyResult), cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException) { context.Response.StatusCode = HttpStatusCode.NotFound; }
        catch (Exception exception) when (exception is JsonException or ArgumentException or UriFormatException) { context.Response.StatusCode = HttpStatusCode.BadRequest; }
        return true;
    }

    private static async ValueTask WriteAsync(IHttpContext context, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-store";
        context.Response.Headers[HttpHeaderKey.ContentType] = "application/json; charset=utf-8";
        if (context.Request.Method != HttpMethod.Head)
        {
            await context.Response.Body.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        }
    }
}

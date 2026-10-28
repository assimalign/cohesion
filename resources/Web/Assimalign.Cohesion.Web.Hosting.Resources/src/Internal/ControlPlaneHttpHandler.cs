using System;
using System.Buffers;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Internal;

/// <summary>
/// Maps the control plane's HTTP routes onto an <see cref="IResourceControlPlane"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>GET /manifest</c>: the instance's manifest.</item>
/// <item><c>GET /actions/{name}</c>: reads an action.</item>
/// <item><c>POST /actions/{name}</c>: writes an action; the body is its JSON input.</item>
/// <item><c>PUT /state/{name}</c>: restores an action's state; the body is the snapshot's JSON value.</item>
/// </list>
/// An action or restore answers with <c>{ "status", "output"?, "reason"?, "state"? }</c>, where
/// <c>state</c> is the snapshot a successful write to a stateful action left. Statuses map to 200
/// (succeeded), 400 (rejected), 404 (no such action), 405 (operation not accepted), and 503 (the
/// control plane is not started).
/// </remarks>
internal static class ControlPlaneHttpHandler
{
    private const string jsonContentType = "application/json";
    private const string actionsPrefix = "/actions/";
    private const string statePrefix = "/state/";

    public static async Task InvokeAsync(IResourceControlPlane controlPlane, IHttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        HttpMethod method = context.Request.Method;
        CancellationToken cancellationToken = context.RequestCancelled;

        if (path == "/manifest")
        {
            if (method != HttpMethod.Get)
            {
                context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
                return;
            }

            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(controlPlane.Manifest, ResourceJsonContext.Default.ResourceManifest);
            await WriteJsonAsync(context, HttpStatusCode.Ok, manifest, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (TryReadName(path, actionsPrefix, out ResourceActionName action))
        {
            ResourceActionKind operation = method == HttpMethod.Get ? ResourceActionKind.Read
                : method == HttpMethod.Post ? ResourceActionKind.Write
                : ResourceActionKind.None;
            if (operation == ResourceActionKind.None)
            {
                context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
                return;
            }

            byte[] input = operation == ResourceActionKind.Write ? await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false) : [];
            ResourceActionResult result;
            try
            {
                result = await controlPlane.ExecuteAsync(action, ResourceActionContext.Create(operation, input), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                await WriteUnavailableAsync(context, exception, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteResultAsync(context, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (TryReadName(path, statePrefix, out action))
        {
            if (method != HttpMethod.Put)
            {
                context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
                return;
            }

            byte[] value = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);
            if (value.Length == 0)
            {
                await WriteResultAsync(context, ResourceActionResult.Rejected("A snapshot value is required."), cancellationToken).ConfigureAwait(false);
                return;
            }

            ResourceActionResult result;
            try
            {
                result = await controlPlane.RestoreAsync(ResourceStateSnapshot.Create(action, value), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                await WriteUnavailableAsync(context, exception, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteResultAsync(context, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = HttpStatusCode.NotFound;
    }

    private static bool TryReadName(string path, string prefix, out ResourceActionName name)
    {
        name = default;
        return path.StartsWith(prefix, StringComparison.Ordinal)
            && ResourceActionName.TryCreate(path[prefix.Length..], out name);
    }

    private static async Task<byte[]> ReadBodyAsync(IHttpContext context, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static Task WriteResultAsync(IHttpContext context, ResourceActionResult result, CancellationToken cancellationToken)
    {
        HttpStatusCode status = result.Status switch
        {
            ResourceActionStatus.Succeeded => HttpStatusCode.Ok,
            ResourceActionStatus.Rejected => HttpStatusCode.BadRequest,
            ResourceActionStatus.NotFound => HttpStatusCode.NotFound,
            _ => HttpStatusCode.MethodNotAllowed,
        };

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", result.Status.ToString());
            if (!result.Output.IsEmpty)
            {
                writer.WritePropertyName("output");
                writer.WriteRawValue(result.Output.Span);
            }

            if (result.Reason is not null)
            {
                writer.WriteString("reason", result.Reason);
            }

            if (result.State is { } state)
            {
                writer.WritePropertyName("state");
                writer.WriteRawValue(state.Value.Span);
            }

            writer.WriteEndObject();
        }

        return WriteJsonAsync(context, status, buffer.WrittenMemory, cancellationToken);
    }

    // The control plane accepts invocations only while started: before its publication completes
    // and after it stops, the instance is unavailable rather than wrong.
    private static Task WriteUnavailableAsync(IHttpContext context, InvalidOperationException exception, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "Unavailable");
            writer.WriteString("reason", exception.Message);
            writer.WriteEndObject();
        }

        return WriteJsonAsync(context, HttpStatusCode.ServiceUnavailable, buffer.WrittenMemory, cancellationToken);
    }

    private static async Task WriteJsonAsync(IHttpContext context, HttpStatusCode status, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.Headers[HttpHeaderKey.ContentType] = jsonContentType;
        await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }
}

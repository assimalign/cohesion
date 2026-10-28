using System;
using System.IO;
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
/// <item><c>POST /actions/{name}</c>: writes an action; the body is its input.</item>
/// <item><c>PUT /state/{name}</c>: restores an action's state; the body is the snapshot's value.</item>
/// </list>
/// Bodies in both directions are written by the control plane's content serializer, and responses
/// carry its media type. An action or restore answers with its <see cref="ResourceActionResult"/>,
/// whose <c>state</c> is the snapshot a successful write to a stateful action left. Statuses map to
/// 200 (succeeded), 400 (rejected), 404 (no such action), 405 (operation not accepted), and 503 with
/// no body (the control plane is not started).
/// </remarks>
internal static class ControlPlaneHttpHandler
{
    private const string actionsPrefix = "/actions/";
    private const string statePrefix = "/state/";

    public static async Task InvokeAsync(IResourceControlPlane controlPlane, IHttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        HttpMethod method = context.Request.Method;
        CancellationToken cancellationToken = context.RequestCancelled;
        IResourceContentSerializer serializer = controlPlane.Serializer;

        if (path == "/manifest")
        {
            if (method != HttpMethod.Get)
            {
                context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
                return;
            }

            await WriteAsync(context, serializer, HttpStatusCode.Ok, serializer.Serialize(controlPlane.Manifest), cancellationToken).ConfigureAwait(false);
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
                result = await controlPlane.ExecuteAsync(action, ResourceActionContext.Create(operation, serializer, input), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                context.Response.StatusCode = HttpStatusCode.ServiceUnavailable;
                return;
            }

            await WriteResultAsync(context, serializer, result, cancellationToken).ConfigureAwait(false);
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
                await WriteResultAsync(context, serializer, ResourceActionResult.Rejected(ErrorMessages.SnapshotValueRequired), cancellationToken).ConfigureAwait(false);
                return;
            }

            ResourceActionResult result;
            try
            {
                result = await controlPlane.RestoreAsync(ResourceStateSnapshot.Create(action, value), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                context.Response.StatusCode = HttpStatusCode.ServiceUnavailable;
                return;
            }

            await WriteResultAsync(context, serializer, result, cancellationToken).ConfigureAwait(false);
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

    private static Task WriteResultAsync(IHttpContext context, IResourceContentSerializer serializer, ResourceActionResult result, CancellationToken cancellationToken)
    {
        HttpStatusCode status = result.Status switch
        {
            ResourceActionStatus.Succeeded => HttpStatusCode.Ok,
            ResourceActionStatus.Rejected => HttpStatusCode.BadRequest,
            ResourceActionStatus.NotFound => HttpStatusCode.NotFound,
            _ => HttpStatusCode.MethodNotAllowed,
        };

        return WriteAsync(context, serializer, status, serializer.Serialize(result), cancellationToken);
    }

    private static async Task WriteAsync(IHttpContext context, IResourceContentSerializer serializer, HttpStatusCode status, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.Headers[HttpHeaderKey.ContentType] = serializer.MediaType;
        await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }
}

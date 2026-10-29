using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Internal;

/// <summary>
/// Maps the control plane's HTTP routes onto an <see cref="IApplicationInstanceControlPlane"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>GET /manifest</c>: the instance's manifest.</item>
/// <item><c>GET /actions/{name}</c>: reads an action.</item>
/// <item><c>POST /actions/{name}</c>: writes an action; the body is its input.</item>
/// <item><c>PUT /state/{name}</c>: restores an action's state; the body is the snapshot's value.</item>
/// </list>
/// Bodies in both directions are written by the control plane's content serializer, and responses
/// carry its media type. An action or restore answers with its <see cref="ApplicationActionResult"/>,
/// whose <c>state</c> is the snapshot a successful write to a stateful action left. Statuses map to
/// 200 (succeeded), 400 (rejected), 404 (no such action), and 405 (operation not accepted). Any other
/// failure is the engine's 500.
/// </remarks>
internal static class ControlPlaneHttpHandler
{
    private const string actionsPrefix = "/actions/";
    private const string statePrefix = "/state/";

    public static async Task InvokeAsync(IApplicationInstanceControlPlane controlPlane, IHttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        HttpMethod method = context.Request.Method;
        CancellationToken cancellationToken = context.RequestCancelled;
        IApplicationContentSerializer serializer = controlPlane.Serializer;

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

        if (TryReadName(path, actionsPrefix, out ActionName action))
        {
            ApplicationActionKind operation = method == HttpMethod.Get ? ApplicationActionKind.Read
                : method == HttpMethod.Post ? ApplicationActionKind.Write
                : ApplicationActionKind.None;
            if (operation == ApplicationActionKind.None)
            {
                context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
                return;
            }

            byte[] input = operation == ApplicationActionKind.Write ? await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false) : [];
            ApplicationActionResult result = await controlPlane.ExecuteAsync(action, ApplicationActionContext.Create(operation, serializer, input), cancellationToken).ConfigureAwait(false);

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
                await WriteResultAsync(context, serializer, ApplicationActionResult.Rejected(ErrorMessages.SnapshotValueRequired), cancellationToken).ConfigureAwait(false);
                return;
            }

            ApplicationActionResult result = await controlPlane.RestoreAsync(ApplicationActionSnapshot.Create(action, value), cancellationToken).ConfigureAwait(false);

            await WriteResultAsync(context, serializer, result, cancellationToken).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = HttpStatusCode.NotFound;
    }

    private static bool TryReadName(string path, string prefix, out ActionName name)
    {
        name = default;
        return path.StartsWith(prefix, StringComparison.Ordinal)
            && ActionName.TryCreate(path[prefix.Length..], out name);
    }

    private static async Task<byte[]> ReadBodyAsync(IHttpContext context, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static Task WriteResultAsync(IHttpContext context, IApplicationContentSerializer serializer, ApplicationActionResult result, CancellationToken cancellationToken)
    {
        HttpStatusCode status = result.Status switch
        {
            ApplicationActionStatus.Succeeded => HttpStatusCode.Ok,
            ApplicationActionStatus.Rejected => HttpStatusCode.BadRequest,
            ApplicationActionStatus.NotFound => HttpStatusCode.NotFound,
            _ => HttpStatusCode.MethodNotAllowed,
        };

        return WriteAsync(context, serializer, status, serializer.Serialize(result), cancellationToken);
    }

    private static async Task WriteAsync(IHttpContext context, IApplicationContentSerializer serializer, HttpStatusCode status, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        context.Response.Headers[HttpHeaderKey.ContentType] = serializer.MediaType;
        await context.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }
}

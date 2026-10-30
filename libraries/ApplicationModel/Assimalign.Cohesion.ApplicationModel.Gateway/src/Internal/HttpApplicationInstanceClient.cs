using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>
/// Calls instances' control planes over HTTP, through the binding every area's control-plane server
/// serves: <c>GET</c> or <c>POST {endpoint}actions/{name}</c> for an action, and
/// <c>PUT {endpoint}state/{name}</c> for a restore.
/// </summary>
/// <remarks>
/// The binding answers every outcome of an action with a result: <c>200</c>, <c>400</c>, <c>404</c>,
/// or <c>405</c>, each with an <see cref="ApplicationActionResult"/> in JSON. Any other status, or one
/// without a result, is no answer, such as the bare <c>404</c> of a server that serves no control
/// plane at the address, and the call throws.
/// </remarks>
internal sealed class HttpApplicationInstanceClient : IApplicationInstanceClient
{
    // The binding's content is JSON, which is also how a snapshot's value was written.
    private const string jsonMediaType = "application/json";

    private readonly HttpClient _client;

    public HttpApplicationInstanceClient(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler, disposeHandler: false);
    }

    /// <summary>
    /// Gets the client every gateway in the process uses unless its builder sets another. Its handler
    /// pools connections and recycles them, so an instance that moves is reached at its new address.
    /// </summary>
    public static HttpApplicationInstanceClient Shared { get; } = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    });

    public async ValueTask<ApplicationActionResult> ExecuteAsync(ApplicationInstanceManifest instance, ActionName action, ApplicationActionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(context);

        bool write = context.Operation == ApplicationActionKind.Write;
        using var request = new HttpRequestMessage(write ? HttpMethod.Post : HttpMethod.Get, Resolve(instance, "actions/", action));
        if (write)
        {
            request.Content = CreateContent(context.Input, context.Serializer.MediaType);
        }

        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ApplicationActionResult> RestoreAsync(ApplicationInstanceManifest instance, ApplicationActionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(snapshot);

        using var request = new HttpRequestMessage(HttpMethod.Put, Resolve(instance, "state/", snapshot.Action))
        {
            Content = CreateContent(snapshot.Value, jsonMediaType),
        };

        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApplicationActionResult> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(jsonMediaType));
        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed))
        {
            throw Unanswered(request, response.StatusCode, inner: null);
        }

        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        ApplicationActionResult? result;
        try
        {
            result = body.Length == 0 ? null : JsonSerializer.Deserialize(body, GatewayJsonContext.Default.ApplicationActionResult);
        }
        catch (JsonException exception)
        {
            throw Unanswered(request, response.StatusCode, exception);
        }

        return result ?? throw Unanswered(request, response.StatusCode, inner: null);
    }

    // The routes resolve against the endpoint, which a control-plane server serves at its root.
    private static Uri Resolve(ApplicationInstanceManifest instance, string route, ActionName action)
    {
        Uri endpoint = instance.ControlPlaneEndpoint
            ?? throw new ArgumentException(Format(ErrorMessages.InstanceControlPlaneMissing, instance.Key, instance.Provider, instance.Name), nameof(instance));
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(Format(ErrorMessages.InstanceControlPlaneUnsupported, endpoint), nameof(instance));
        }

        return new Uri(endpoint, string.Concat(route, Uri.EscapeDataString(action.Value)));
    }

    private static ReadOnlyMemoryContent CreateContent(ReadOnlyMemory<byte> body, string mediaType)
    {
        var content = new ReadOnlyMemoryContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    private static HttpRequestException Unanswered(HttpRequestMessage request, HttpStatusCode status, Exception? inner)
    {
        return new HttpRequestException(Format(ErrorMessages.InstanceAnswerMissing, request.RequestUri, (int)status), inner, status);
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }
}

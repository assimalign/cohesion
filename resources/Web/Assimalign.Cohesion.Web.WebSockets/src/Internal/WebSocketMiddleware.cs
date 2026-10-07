using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.WebSockets.Internal;

/// <summary>
/// The middleware <c>UseWebSockets</c> registers. An ordinary request passes straight through. A
/// WebSocket handshake attempt is refused when it is malformed (<c>400</c>, or <c>426</c> for
/// another version) or comes from a disallowed origin (<c>403</c>); a valid one continues with
/// <see cref="WebSocketPolicyFeature"/> installed, so the accept downstream takes the policy.
/// </summary>
internal sealed class WebSocketMiddleware : IWebApplicationMiddleware
{
    private readonly WebSocketPolicy _policy;

    public WebSocketMiddleware(WebSocketPolicy policy)
    {
        _policy = policy;
    }

    /// <inheritdoc />
    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        IHttpWebSocketFeature webSockets = context.WebSockets;

        switch (webSockets.HandshakeStatus)
        {
            case HttpWebSocketHandshakeStatus.None:
                return next(context);

            case HttpWebSocketHandshakeStatus.Valid:
                break;

            default:
                // RFC 6455 §4.2.1: a server that speaks WebSockets refuses a broken handshake rather
                // than serving the request as an ordinary one.
                webSockets.RejectHandshake();
                return Task.CompletedTask;
        }

        // Cross-site WebSocket hijacking: a page on another site would otherwise open a socket that
        // carries the user's cookies.
        if (!_policy.IsOriginAllowed(context))
        {
            context.Response.StatusCode = HttpStatusCode.Forbidden;
            return Task.CompletedTask;
        }

        return InvokeWithPolicyAsync(context, webSockets, next);
    }

    private async Task InvokeWithPolicyAsync(IHttpContext context, IHttpWebSocketFeature webSockets, WebApplicationMiddleware next)
    {
        WebSocketPolicyFeature feature = new(webSockets, _policy, context.Features.Get<IWebServerDrainFeature>());
        context.Features.Set(feature);

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            feature.Release();
        }
    }
}

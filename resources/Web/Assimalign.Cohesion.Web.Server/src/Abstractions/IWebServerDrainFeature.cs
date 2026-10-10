using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// The server's drain signal, published to the current exchange: a token cancelled when the
/// server begins its lame-duck drain.
/// </summary>
/// <remarks>
/// <para>
/// A drain stops new work and lets the exchanges in flight finish within the stop's budget. An
/// ordinary exchange finishes on its own, but a long-lived one — a WebSocket, a stream of
/// server-sent events — runs until something ends it. This feature lets such an exchange end its
/// own work cleanly while the budget lasts, for example by closing a WebSocket with
/// <c>1001 Going Away</c>, rather than being cut off when the budget runs out.
/// </para>
/// <para>
/// <see cref="Draining"/> is not the exchange's cancellation. The exchange keeps running after it
/// fires, and its response is still delivered. <see cref="IHttpContext.RequestCancelled"/> fires
/// only if the exchange is still running when the budget runs out.
/// </para>
/// <para>
/// The default Web server installs this feature on every exchange. A custom
/// <see cref="IWebApplicationServer"/> may omit it, so consumers must handle an absent feature.
/// </para>
/// </remarks>
public interface IWebServerDrainFeature : IHttpFeature
{
    /// <summary>
    /// Gets a token that is cancelled when the server begins draining. A callback registered after
    /// the drain began runs at once.
    /// </summary>
    CancellationToken Draining { get; }
}

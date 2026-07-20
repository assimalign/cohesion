using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Sessions.Internal;

namespace Assimalign.Cohesion.Web.Sessions;

/// <summary>
/// Async session accessors on <see cref="IHttpContext"/> for applications served
/// behind <c>UseSessions</c>. These are the ergonomic entry points that honor the
/// lazy load-on-first-access lifecycle without blocking on the store.
/// </summary>
/// <remarks>
/// The synchronous <c>context.Session</c> accessor (from
/// <c>Assimalign.Cohesion.Http.Sessions</c>) still works to reach the installed
/// session, but a store-backed session is not readable until it has been loaded;
/// prefer <see cref="LoadSessionAsync"/>, which materializes, establishes the
/// cookie for a new session, and loads in one awaited call.
/// </remarks>
public static class SessionContextExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Establishes the session (minting and cookie-issuing a new id on first
        /// access) and loads it from the store, returning the ready session. The
        /// first call performs the store read; subsequent calls return the already
        /// loaded session.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the load.</param>
        /// <returns>The loaded session.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Session support is not enabled (no <c>UseSessions</c> in the pipeline).</exception>
        public ValueTask<IHttpSession> LoadSessionAsync(CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ResolveWebSessionFeature(context).EstablishAndLoadAsync(cancellationToken);
        }

        /// <summary>
        /// Regenerates the current session's id while preserving its state — the
        /// standard defense against session fixation, to be called immediately
        /// after a privilege change such as authentication. A fresh cryptographically
        /// random id is minted, the old id is removed from the store, and the
        /// session-id cookie is replaced.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the store removal.</param>
        /// <returns>A task that completes when the id has been regenerated.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// Session support is not enabled, no session has been established on this
        /// request, or the response head has already started (the cookie can no
        /// longer be replaced — regenerate before writing the response).
        /// </exception>
        public ValueTask RegenerateSessionIdAsync(CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            return ResolveWebSessionFeature(context).RegenerateIdAsync(cancellationToken);
        }
    }

    private static WebSessionFeature ResolveWebSessionFeature(IHttpContext context)
        => context.Features.Get<IHttpSessionFeature>() as WebSessionFeature
            ?? throw new InvalidOperationException(
                "Session support is not enabled for this request. Call UseSessions() in the application pipeline before accessing sessions.");
}

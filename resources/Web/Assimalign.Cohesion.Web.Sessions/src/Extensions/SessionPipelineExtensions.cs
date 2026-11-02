using System;
using System.Linq;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Sessions.Internal;

namespace Assimalign.Cohesion.Web.Sessions;

/// <summary>
/// Pipeline-builder members that place per-request HTTP session support in a web
/// application.
/// </summary>
/// <remarks>
/// <para>
/// Place <c>UseSessions</c> before any middleware or endpoint that reads or
/// writes the session, and — because a new session establishes its cookie
/// synchronously on first access — before the response head is committed.
/// </para>
/// <para>
/// The cookie, idle timeout, and store are configured where the middleware is
/// registered, <c>builder.Services.AddSessions(sessions =&gt; ...)</c>
/// (<see cref="SessionsBuilder"/>); this verb only positions it. Composition is
/// dependency-free and no request-time service location occurs.
/// </para>
/// <para>
/// The session-id cookie carries <c>Secure</c> whenever the effective scheme
/// (<see cref="HttpContextForwardedExtensions.EffectiveScheme"/>) is HTTPS. Behind a
/// TLS-terminating proxy, place <c>UseForwardedHeaders</c> ahead of
/// <c>UseSessions</c> so the proxy's <c>https</c> is known when the cookie is
/// established; without it the transport-derived scheme decides.
/// </para>
/// </remarks>
public static class SessionPipelineExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the session middleware the application registered with
        /// <c>builder.Services.AddSessions(sessions =&gt; ...)</c>, or one with the defaults when it
        /// registered none: the <c>.Cohesion.Session</c> cookie, a 20-minute idle timeout, and a new
        /// in-memory session store.
        /// </summary>
        /// <remarks>
        /// When the application registered the middleware more than once, the last registration wins.
        /// Placing the verb twice places the same registered instance, so both positions share one store.
        /// </remarks>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseSessions()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<SessionMiddleware>().LastOrDefault()
                    ?? new SessionsBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}

using System;
using System.Globalization;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Sessions.Internal;

namespace Assimalign.Cohesion.Web.Sessions;

/// <summary>
/// Pipeline-builder members that add per-request HTTP session support to a web
/// application.
/// </summary>
/// <remarks>
/// <para>
/// Register <c>UseSessions</c> before any middleware or endpoint that reads or
/// writes the session, and — because a new session establishes its cookie
/// synchronously on first access — before the response head is committed.
/// Composition is dependency-free: the store and options are captured at builder
/// time and no request-time service location occurs.
/// </para>
/// <para>
/// With no store argument the middleware uses an in-process
/// <see cref="InMemoryHttpSessionStore"/>. An out-of-process deployment (behind a
/// load balancer, without sticky sessions) supplies a distributed
/// <see cref="IHttpSessionStore"/> adapter through the store overload; nothing
/// else in the pipeline changes.
/// </para>
/// <para>
/// The session-id cookie carries <c>Secure</c> whenever the effective scheme
/// (<see cref="HttpContextForwardedExtensions.EffectiveScheme"/>) is HTTPS. Behind a
/// TLS-terminating proxy, register <c>UseForwardedHeaders</c> ahead of
/// <c>UseSessions</c> so the proxy's <c>https</c> is known when the cookie is
/// established; without it the transport-derived scheme decides.
/// </para>
/// </remarks>
public static class SessionPipelineExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the session middleware backed by an in-memory session store.
        /// </summary>
        /// <param name="configure">An optional callback to configure the cookie name/path/HttpOnly flag and the idle timeout.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// The configured <see cref="HttpSessionOptions.IdleTimeout"/> is not positive,
        /// <see cref="HttpSessionOptions.CookieName"/> is not an RFC 6265 <c>token</c>, or
        /// <see cref="HttpSessionOptions.CookiePath"/> is not an RFC 6265 <c>path-value</c>.
        /// </exception>
        public IWebApplicationPipelineBuilder UseSessions(Action<HttpSessionOptions>? configure = null)
            => builder.UseSessions(new InMemoryHttpSessionStore(), configure);

        /// <summary>
        /// Adds the session middleware backed by the supplied session store.
        /// </summary>
        /// <param name="store">The backing store the session state round-trips through.</param>
        /// <param name="configure">An optional callback to configure the cookie name/path/HttpOnly flag and the idle timeout.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="store"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// The configured <see cref="HttpSessionOptions.IdleTimeout"/> is not positive,
        /// <see cref="HttpSessionOptions.CookieName"/> is not an RFC 6265 <c>token</c>, or
        /// <see cref="HttpSessionOptions.CookiePath"/> is not an RFC 6265 <c>path-value</c>.
        /// </exception>
        public IWebApplicationPipelineBuilder UseSessions(IHttpSessionStore store, Action<HttpSessionOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(store);

            HttpSessionOptions options = new();
            configure?.Invoke(options);

            if (options.IdleTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.SessionIdleTimeoutMustBePositive, options.IdleTimeout),
                    nameof(configure));
            }

            // Reuse the cookie constructor's token check at composition, before any middleware is registered.
            try
            {
                _ = new HttpCookie(options.CookieName);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    ErrorMessages.SessionCookieNameInvalid,
                    nameof(configure),
                    exception);
            }

            // The session cookie is built each time a session is established, and HttpCookieOptions refuses
            // a Path outside the RFC 6265 path-value grammar (#1153). Checking the path here fails the
            // application at composition instead of failing every session establishment with a 500.
            try
            {
                _ = new HttpCookieOptions { Path = options.CookiePath };
            }
            catch (ArgumentException exception)
            {
                // The value is not echoed: it may hold the control character that made it invalid.
                throw new ArgumentException(
                    ErrorMessages.SessionCookiePathInvalid,
                    nameof(configure),
                    exception);
            }

            return builder.Use(new SessionMiddleware(store, options));
        }
    }
}

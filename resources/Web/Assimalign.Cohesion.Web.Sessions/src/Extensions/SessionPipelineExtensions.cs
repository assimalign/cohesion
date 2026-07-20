using System;

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
        /// <exception cref="ArgumentException">The configured <see cref="HttpSessionOptions.IdleTimeout"/> is not positive.</exception>
        public IWebApplicationPipelineBuilder UseSessions(Action<HttpSessionOptions>? configure = null)
            => builder.UseSessions(new InMemoryHttpSessionStore(), configure);

        /// <summary>
        /// Adds the session middleware backed by the supplied session store.
        /// </summary>
        /// <param name="store">The backing store the session state round-trips through.</param>
        /// <param name="configure">An optional callback to configure the cookie name/path/HttpOnly flag and the idle timeout.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="store"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The configured <see cref="HttpSessionOptions.IdleTimeout"/> is not positive.</exception>
        public IWebApplicationPipelineBuilder UseSessions(IHttpSessionStore store, Action<HttpSessionOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(store);

            HttpSessionOptions options = new();
            configure?.Invoke(options);

            if (options.IdleTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentException(
                    $"HttpSessionOptions.IdleTimeout must be positive; got {options.IdleTimeout}.",
                    nameof(configure));
            }

            return builder.Use(new SessionMiddleware(store, options));
        }
    }
}

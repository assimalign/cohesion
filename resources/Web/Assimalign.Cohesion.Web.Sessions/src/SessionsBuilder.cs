using System;
using System.Globalization;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Sessions.Internal;

namespace Assimalign.Cohesion.Web.Sessions;

/// <summary>
/// Configures the session middleware that <c>builder.Services.AddSessions(sessions =&gt; ...)</c>
/// registers and <see cref="SessionPipelineExtensions.UseSessions(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Build"/> validates the options once, so a non-positive idle timeout or a
/// <see langword="default"/> cookie name or path fails where the application registers sessions, never
/// on a request.
/// </para>
/// <para>
/// Without <see cref="UseStore"/> the middleware keeps session state in a new in-process
/// <see cref="InMemoryHttpSessionStore"/>. An out-of-process deployment (behind a load balancer, without
/// sticky sessions) supplies a distributed <see cref="IHttpSessionStore"/> adapter instead; nothing else
/// in the pipeline changes.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SessionsBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class SessionsBuilder
{
    private IHttpSessionStore? _store;

    /// <summary>
    /// Initializes a builder with the defaults: the <c>.Cohesion.Session</c> cookie on path <c>/</c>,
    /// <c>HttpOnly</c> and not essential, a 20-minute idle timeout, and an in-memory store.
    /// </summary>
    public SessionsBuilder()
    {
    }

    /// <summary>
    /// Gets the session options: the cookie name, path, <c>HttpOnly</c> flag, and consent essentiality,
    /// and the idle timeout.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpSessionOptions.CookieName"/> and <see cref="HttpSessionOptions.CookiePath"/> refuse an
    /// out-of-grammar string at assignment, with parameter name <c>value</c>. The middleware reads its
    /// options on every request, so <see cref="Build"/> gives it a copy of these; a change made afterwards
    /// reaches only a later <see cref="Build"/>.
    /// </remarks>
    public HttpSessionOptions Options { get; } = new();

    /// <summary>
    /// Backs the sessions with <paramref name="store"/> instead of an in-memory store the middleware creates.
    /// </summary>
    /// <remarks>
    /// The application owns the store; the middleware never disposes it. A later call replaces the store an
    /// earlier call supplied.
    /// </remarks>
    /// <param name="store">The store session state round-trips through.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public SessionsBuilder UseStore(IHttpSessionStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        return this;
    }

    /// <summary>
    /// Validates <see cref="Options"/> and builds the session middleware from a copy of them, over the
    /// store <see cref="UseStore"/> supplied or a new <see cref="InMemoryHttpSessionStore"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which installs a lazy, store-backed session on each exchange and commits it after
    /// the pipeline unwinds.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <see cref="HttpSessionOptions.IdleTimeout"/> is not positive, or
    /// <see cref="HttpSessionOptions.CookieName"/> or <see cref="HttpSessionOptions.CookiePath"/> is
    /// <see langword="default"/>; the parameter name is <c>Options</c>.
    /// </exception>
    public IWebApplicationMiddleware Build()
    {
        if (Options.IdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.SessionIdleTimeoutInvalid, Options.IdleTimeout),
                nameof(Options));
        }

        // The value types reject out-of-grammar strings at assignment; default is the
        // one value they cannot see. Refuse it before any middleware is built.
        if (Options.CookieName.Value is null)
        {
            throw new ArgumentException(ErrorMessages.SessionCookieNameInvalid, nameof(Options));
        }

        if (Options.CookiePath.Value is null)
        {
            throw new ArgumentException(ErrorMessages.SessionCookiePathInvalid, nameof(Options));
        }

        return new SessionMiddleware(_store ?? new InMemoryHttpSessionStore(), Snapshot(Options));
    }

    // Copies every HttpSessionOptions member; an option added there needs a line here, or it reaches the
    // middleware only at its default.
    private static HttpSessionOptions Snapshot(HttpSessionOptions options) => new()
    {
        CookieName = options.CookieName,
        CookiePath = options.CookiePath,
        CookieHttpOnly = options.CookieHttpOnly,
        CookieIsEssential = options.CookieIsEssential,
        IdleTimeout = options.IdleTimeout,
    };

    private sealed class DebugView
    {
        private readonly SessionsBuilder _target;

        public DebugView(SessionsBuilder value)
        {
            _target = value;
        }

        public HttpSessionOptions Options => _target.Options;
    }
}

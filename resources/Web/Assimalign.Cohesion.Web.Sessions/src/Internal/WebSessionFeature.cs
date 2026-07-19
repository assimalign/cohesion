using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Sessions.Internal;

/// <summary>
/// The per-exchange session feature installed by the session middleware. It is
/// <em>lazy</em>: no store I/O and no cookie are produced until the application
/// first touches the session. On that first access it resolves the session id
/// (from the request cookie, or a freshly minted cryptographically random id),
/// synchronously establishes the session-id cookie when the id is new, and
/// creates the store-backed session; the application then loads and mutates it,
/// and the middleware commits it after the pipeline unwinds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cookie timing.</b> The <c>Set-Cookie</c> for a new id is appended at
/// establishment time — synchronously, when the id is first minted — so it is on
/// the response before the head commits. Establishment is skipped (best-effort)
/// if the response head has already started, since a committed head can carry no
/// new field. The post-<c>next</c> commit path touches only the store, never
/// headers.
/// </para>
/// <para>
/// When the request already carries a valid session cookie, no new
/// <c>Set-Cookie</c> is emitted (the client already holds the id); the server
/// slides the store's idle window instead.
/// </para>
/// </remarks>
internal sealed class WebSessionFeature : IHttpSessionFeature
{
    private readonly IHttpContext _context;
    private readonly IHttpSessionStore _store;
    private readonly HttpSessionOptions _options;

    private IHttpSession? _current;
    private HttpSessionStoreSession? _storeSession;

    public WebSessionFeature(IHttpContext context, IHttpSessionStore store, HttpSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _context = context;
        _store = store;
        _options = options;
    }

    // Occupy the canonical session slot so context.Session resolves to this feature.
    public string Name => nameof(HttpSession);

    /// <summary>
    /// Gets a value indicating whether the application touched the session on
    /// this request. The middleware commits only when this is
    /// <see langword="true"/>, so an untouched session performs no I/O and mints
    /// no cookie.
    /// </summary>
    public bool WasAccessed => _current is not null;

    /// <inheritdoc />
    public IHttpSession Session
    {
        get => _current ??= MaterializeStoreSession();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _current = value;
            // An externally-supplied session replaces our managed one; regeneration
            // (which needs the store-backed type) no longer applies to it.
            _storeSession = value as HttpSessionStoreSession;
        }
    }

    /// <summary>
    /// Materializes the session (establishing the cookie for a new id) and loads
    /// it from the store on first access. Subsequent calls return the already
    /// loaded session.
    /// </summary>
    public async ValueTask<IHttpSession> EstablishAndLoadAsync(CancellationToken cancellationToken)
    {
        IHttpSession session = Session;
        if (!session.IsAvailable)
        {
            await session.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        return session;
    }

    /// <summary>
    /// Commits the session after the pipeline unwinds: persists it when modified,
    /// otherwise slides the store's idle window for an accessed session. A no-op
    /// when the session was never accessed.
    /// </summary>
    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        if (_current is null)
        {
            return;
        }

        await _current.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Regenerates the session id, keeping the current state (post-authentication
    /// fixation defense): a new id is minted, the old id is removed from the
    /// store, the buffered state is re-keyed to the new id, and the session-id
    /// cookie is replaced.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No store-backed session has been established, or the response head has
    /// already started (the cookie can no longer be replaced).
    /// </exception>
    public async ValueTask RegenerateIdAsync(CancellationToken cancellationToken)
    {
        if (_storeSession is null)
        {
            throw new InvalidOperationException(
                "No session has been established on this request; access the session before regenerating its id.");
        }

        if (ResponseHeadStarted())
        {
            throw new InvalidOperationException(
                "The session id cannot be regenerated after the response has started; regenerate it before writing the response.");
        }

        string oldId = _storeSession.Id;
        string newId = SessionId.Create();

        await _store.RemoveAsync(oldId, cancellationToken).ConfigureAwait(false);
        _storeSession.ReassignId(newId);
        EstablishCookie(newId);
    }

    private IHttpSession MaterializeStoreSession()
    {
        string? existingId = ReadRequestSessionId();

        string id;
        if (existingId is not null)
        {
            // The client already holds this id; do not re-issue the cookie.
            id = existingId;
        }
        else
        {
            id = SessionId.Create();
            EstablishCookie(id);
        }

        HttpSessionStoreSession session = new(id, _store, _options.IdleTimeout);
        _storeSession = session;
        return session;
    }

    private string? ReadRequestSessionId()
    {
        foreach (HttpCookie cookie in _context.Request.Cookies)
        {
            if (string.Equals(cookie.Name, _options.CookieName, StringComparison.Ordinal))
            {
                return string.IsNullOrEmpty(cookie.Value) ? null : cookie.Value;
            }
        }

        return null;
    }

    private void EstablishCookie(string id)
    {
        // A committed head can carry no new Set-Cookie; skip rather than fault. The
        // session still functions in memory for the remainder of the request.
        if (ResponseHeadStarted())
        {
            return;
        }

        IHttpCookieCollection cookies = _context.Response.Cookies;
        RemoveQueuedSessionCookies(cookies);
        cookies.Add(BuildCookie(id));
    }

    private HttpCookie BuildCookie(string id)
    {
        HttpCookieOptions cookieOptions = new()
        {
            Path = _options.CookiePath,
            HttpOnly = _options.CookieHttpOnly,
            SameSite = HttpCookieSameSiteMode.Lax,
            // Secure is bound to the transport-derived scheme: emitted only over HTTPS.
            Secure = _context.Request.Scheme == HttpScheme.Https,
            // Session-scoped: no Expires / Max-Age, so the cookie clears when the
            // browser session ends. Server-side idle timeout governs expiry.
        };

        return new HttpCookie(_options.CookieName, id, cookieOptions);
    }

    private void RemoveQueuedSessionCookies(IHttpCookieCollection cookies)
    {
        List<HttpCookie>? stale = null;
        foreach (HttpCookie existing in cookies)
        {
            if (string.Equals(existing.Name, _options.CookieName, StringComparison.Ordinal))
            {
                (stale ??= []).Add(existing);
            }
        }

        if (stale is not null)
        {
            foreach (HttpCookie existing in stale)
            {
                cookies.Remove(existing);
            }
        }
    }

    private bool ResponseHeadStarted()
        => _context.Response.Headers.IsReadOnly
            || _context.Features.Get<IHttpResponseStreamingFeature>() is { HasStarted: true };
}

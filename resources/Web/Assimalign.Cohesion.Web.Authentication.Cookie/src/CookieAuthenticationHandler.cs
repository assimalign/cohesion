using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Authentication.Cookie.Internal;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Authentication.Cookie;

// Deviates from the internal-implementation rule per owner decision 59 (#1465): the implementation is public.
/// <summary>
/// The cookie authentication handler: issues and validates a data-protected ticket cookie,
/// applies sliding-expiration renewal, and drives login/logout/access-denied behavior — redirects
/// for interactive endpoints, bare <c>401</c>/<c>403</c> for API endpoints.
/// </summary>
/// <remarks>
/// Proxy posture: the only transport fact the handler consults is the effective scheme
/// (<see cref="HttpContextForwardedExtensions.EffectiveScheme"/>), which puts a <c>Secure</c> floor
/// under every cookie it emits over HTTPS — including HTTPS terminated at a trusted proxy. Redirect
/// <c>Location</c> values are relative references (the configured path plus the request path as the
/// return URL), which the user agent resolves against the URL it actually requested, so they need no
/// host or scheme and stay correct behind a proxy.
/// </remarks>
public sealed class CookieAuthenticationHandler : IAuthenticationSignInHandler
{
    private static readonly string _authenticationCookieEncodingInvalidMessage = ErrorMessages.AuthenticationCookieEncodingInvalid;
    private static readonly string _authenticationCookiePayloadMalformedMessage = ErrorMessages.AuthenticationCookiePayloadMalformed;
    private static readonly string _authenticationCookieUnprotectFailedMessage = ErrorMessages.AuthenticationCookieUnprotectFailed;
    private static readonly string _authenticationTicketExpiredMessage = ErrorMessages.AuthenticationTicketExpired;

    private readonly CookieAuthenticationOptions _options;
    private readonly IDataProtector _protector;

    private AuthenticationScheme _scheme = null!;
    private IHttpContext _context = null!;
    private AuthenticateResult? _cachedResult;

    private CookieAuthenticationHandler(CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Conversion validates every assigned string; default bypasses the value type's constructor.
        if (options.CookieName.Value is null)
        {
            throw new ArgumentException(ErrorMessages.CookieNameInvalid, nameof(options));
        }

        _options = options;
        _protector = options.TicketProtector
            ?? throw new InvalidOperationException(
                ErrorMessages.TicketProtectorRequired);
    }

    /// <summary>
    /// Creates a cookie authentication handler over the supplied options.
    /// </summary>
    /// <param name="options">The configured cookie options. <see cref="CookieAuthenticationOptions.TicketProtector"/> must be set.</param>
    /// <returns>A cookie authentication handler.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="CookieAuthenticationOptions.CookieName"/> is <see langword="default"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="CookieAuthenticationOptions.TicketProtector"/> is not set.</exception>
    public static CookieAuthenticationHandler Create(CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CookieAuthenticationHandler(options);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="scheme"/> or <paramref name="context"/> is <see langword="null"/>.</exception>
    public Task InitializeAsync(AuthenticationScheme scheme, IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(context);

        _scheme = scheme;
        _context = context;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AuthenticateResult> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedResult is not null)
        {
            return Task.FromResult(_cachedResult);
        }

        return Task.FromResult(Cache(AuthenticateCore()));
    }

    private AuthenticateResult AuthenticateCore()
    {
        string? cookieValue = ReadRequestCookie(_options.CookieName);
        if (string.IsNullOrEmpty(cookieValue))
        {
            return AuthenticateResult.NoResult();
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Base64Url.DecodeFromChars(cookieValue);
        }
        catch (FormatException exception)
        {
            return Reject("NotBase64Url", _authenticationCookieEncodingInvalidMessage, exception);
        }

        byte[] plaintext;
        try
        {
            plaintext = _protector.Unprotect(protectedBytes);
        }
        catch (DataProtectionException exception)
        {
            // Tampered, foreign-purpose, or aged-out-of-grace ticket.
            return Reject("UnprotectFailed", _authenticationCookieUnprotectFailedMessage, exception);
        }

        AuthenticationTicket ticket;
        try
        {
            ticket = CookieTicketSerializer.Deserialize(plaintext, _scheme.Name);
        }
        catch (FormatException exception)
        {
            return Reject("PayloadMalformed", _authenticationCookiePayloadMalformedMessage, exception);
        }

        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        DateTimeOffset? expiresUtc = ticket.Properties.ExpiresUtc;
        if (expiresUtc.HasValue && expiresUtc.Value <= now)
        {
            return Reject("Expired", _authenticationTicketExpiredMessage);
        }

        RenewIfNeeded(ticket, now);

        return AuthenticateResult.Success(ticket);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="user"/> is <see langword="null"/>.</exception>
    public Task SignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        AuthenticationProperties resolved = properties?.Clone() ?? new AuthenticationProperties();
        resolved.IssuedUtc ??= now;

        if (resolved.ExpiresUtc is null && _options.ExpireTimeSpan > TimeSpan.Zero)
        {
            resolved.ExpiresUtc = resolved.IssuedUtc.Value + _options.ExpireTimeSpan;
        }

        AuthenticationTicket ticket = new(user, resolved, _scheme.Name);
        IssueCookie(ticket, now);

        // Sign-in has immediate effect on the current request's principal.
        _context.User = user;
        _cachedResult = AuthenticateResult.Success(ticket);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SignOutAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        // The deletion cookie carries the same attributes as the issued one, Secure floor included.
        HttpCookieOptions deletionOptions = CreateCookieOptions();
        deletionOptions.Expires = DateTimeOffset.UnixEpoch;
        deletionOptions.MaxAge = TimeSpan.Zero;

        SetResponseCookie(new HttpCookie(_options.CookieName, string.Empty, deletionOptions));
        _cachedResult = AuthenticateResult.NoResult();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ChallengeAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        if (IsApiEndpoint())
        {
            _context.Response.StatusCode = HttpStatusCode.Unauthorized;
            return Task.CompletedTask;
        }

        string returnUrl = properties?.RedirectUri ?? CurrentRequestPath();
        _context.Response.Headers[HttpHeaderKey.Location] =
            AppendReturnUrl(_options.LoginPath.Value, _options.ReturnUrlParameter, returnUrl);
        _context.Response.StatusCode = HttpStatusCode.Found;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ForbidAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default)
    {
        if (IsApiEndpoint())
        {
            _context.Response.StatusCode = HttpStatusCode.Forbidden;
            return Task.CompletedTask;
        }

        string returnUrl = properties?.RedirectUri ?? CurrentRequestPath();
        _context.Response.Headers[HttpHeaderKey.Location] =
            AppendReturnUrl(_options.AccessDeniedPath.Value, _options.ReturnUrlParameter, returnUrl);
        _context.Response.StatusCode = HttpStatusCode.Found;
        return Task.CompletedTask;
    }

    private void RenewIfNeeded(AuthenticationTicket ticket, DateTimeOffset now)
    {
        if (!_options.SlidingExpiration || ticket.Properties.AllowRefresh == false)
        {
            return;
        }

        DateTimeOffset? issued = ticket.Properties.IssuedUtc;
        DateTimeOffset? expires = ticket.Properties.ExpiresUtc;
        if (issued is null || expires is null)
        {
            return;
        }

        TimeSpan window = expires.Value - issued.Value;
        if (window <= TimeSpan.Zero)
        {
            return;
        }

        // Renew once past the midpoint of the ticket's lifetime (RFC-free, matches ASP.NET Core).
        if (now < issued.Value + new TimeSpan(window.Ticks / 2))
        {
            return;
        }

        ticket.Properties.IssuedUtc = now;
        ticket.Properties.ExpiresUtc = now + window;
        IssueCookie(ticket, now);
    }

    private void IssueCookie(AuthenticationTicket ticket, DateTimeOffset now)
    {
        byte[] plaintext = CookieTicketSerializer.Serialize(ticket);
        byte[] protectedBytes = _protector.Protect(plaintext);
        string encoded = Base64Url.EncodeToString(protectedBytes);

        bool persistent = ticket.Properties.IsPersistent ?? false;
        HttpCookieOptions cookieOptions = CreateCookieOptions();

        if (persistent && ticket.Properties.ExpiresUtc is DateTimeOffset expires)
        {
            cookieOptions.Expires = expires;
            TimeSpan maxAge = expires - now;
            cookieOptions.MaxAge = maxAge > TimeSpan.Zero ? maxAge : TimeSpan.Zero;
        }
        else
        {
            // Session cookie: no Expires/Max-Age so it clears when the browser session ends.
            cookieOptions.Expires = null;
            cookieOptions.MaxAge = null;
        }

        SetResponseCookie(new HttpCookie(_options.CookieName, encoded, cookieOptions));
    }

    /// <summary>
    /// Copies the configured cookie template and applies the transport-security floor: whenever the
    /// client reached the application over HTTPS the cookie is marked <c>Secure</c>, whatever the
    /// template says. "Over HTTPS" is the effective scheme, so a trusted TLS-terminating proxy
    /// resolved by the forwarded-headers middleware counts; without that middleware it is the
    /// transport-derived scheme. A template with <c>Secure = true</c> stays Secure on every request.
    /// </summary>
    private HttpCookieOptions CreateCookieOptions()
    {
        HttpCookieOptions cookieOptions = new(_options.Cookie);
        if (_context.EffectiveScheme == HttpScheme.Https)
        {
            cookieOptions.Secure = true;
        }

        return cookieOptions;
    }

    private string? ReadRequestCookie(HttpCookieName name)
    {
        foreach (HttpCookie cookie in _context.Request.Cookies)
        {
            if (cookie.Name == name)
            {
                return cookie.Value;
            }
        }

        return null;
    }

    private void SetResponseCookie(HttpCookie cookie)
    {
        IHttpCookieCollection responseCookies = _context.Response.Cookies;

        // Replace any already-queued cookie with the same name so a re-issue (or a sign-out after
        // a renewal in the same request) emits exactly one Set-Cookie line for the name.
        List<HttpCookie>? stale = null;
        foreach (HttpCookie existing in responseCookies)
        {
            if (existing.Name == cookie.Name)
            {
                (stale ??= new List<HttpCookie>()).Add(existing);
            }
        }

        if (stale is not null)
        {
            foreach (HttpCookie existing in stale)
            {
                responseCookies.Remove(existing);
            }
        }

        responseCookies.Add(cookie);
    }

    private bool IsApiEndpoint()
        => _context.GetEndpointMetadata<ApiEndpointMetadata>() is not null;

    // The return URL is a URL: the login page redirects to it, so the path is written percent-encoded, as the
    // request carried it. The transports hand the path over decoded, and "/café" or "/100%" in a Location is not
    // a URI reference (RFC 9110 §10.2.2); the HTTP/1.1 writer would send "/caf?".
    private string CurrentRequestPath()
    {
        string path = _context.Request.Path.ToUriComponent();
        return string.IsNullOrEmpty(path) ? "/" : path;
    }

    private static string AppendReturnUrl(string basePath, string parameterName, string returnUrl)
    {
        char separator = basePath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{basePath}{separator}{parameterName}={Uri.EscapeDataString(returnUrl)}";
    }

    private AuthenticateResult Reject(string reason, string message, Exception? exception = null)
    {
        CookieAuthenticationEventSource.Log.TicketRejected(_context, _scheme.Name, reason, exception);
        return AuthenticateResult.Fail(message);
    }

    private AuthenticateResult Cache(AuthenticateResult result)
    {
        _cachedResult = result;
        return result;
    }
}

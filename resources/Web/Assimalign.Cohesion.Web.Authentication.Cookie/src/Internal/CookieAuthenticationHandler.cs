using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Authentication.Cookie;

/// <summary>
/// The cookie authentication handler: issues and validates a data-protected ticket cookie,
/// applies sliding-expiration renewal, and drives login/logout/access-denied behavior — redirects
/// for interactive endpoints, bare <c>401</c>/<c>403</c> for API endpoints.
/// </summary>
internal sealed class CookieAuthenticationHandler : IAuthenticationSignInHandler
{
    private readonly CookieAuthenticationOptions _options;
    private readonly IDataProtector _protector;

    private AuthenticationScheme _scheme = null!;
    private IHttpContext _context = null!;
    private AuthenticateResult? _cachedResult;

    public CookieAuthenticationHandler(CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _protector = options.TicketProtector
            ?? throw new InvalidOperationException(
                "CookieAuthenticationOptions.TicketProtector must be set. The composition root wires it " +
                "from the application data-protection key ring.");
    }

    /// <inheritdoc />
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
        catch (FormatException)
        {
            return AuthenticateResult.Fail("The authentication cookie is not valid base64url.");
        }

        byte[] plaintext;
        try
        {
            plaintext = _protector.Unprotect(protectedBytes);
        }
        catch (DataProtectionException)
        {
            // Tampered, foreign-purpose, or aged-out-of-grace ticket.
            return AuthenticateResult.Fail("The authentication cookie could not be unprotected.");
        }

        AuthenticationTicket ticket;
        try
        {
            ticket = CookieTicketSerializer.Deserialize(plaintext, _scheme.Name);
        }
        catch (FormatException)
        {
            return AuthenticateResult.Fail("The authentication cookie payload is malformed.");
        }

        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        DateTimeOffset? expiresUtc = ticket.Properties.ExpiresUtc;
        if (expiresUtc.HasValue && expiresUtc.Value <= now)
        {
            return AuthenticateResult.Fail("The authentication ticket has expired.");
        }

        RenewIfNeeded(ticket, now);

        return AuthenticateResult.Success(ticket);
    }

    /// <inheritdoc />
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
        HttpCookieOptions deletionOptions = new(_options.Cookie)
        {
            Expires = DateTimeOffset.UnixEpoch,
            MaxAge = TimeSpan.Zero,
        };

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
            AppendReturnUrl(_options.LoginPath, _options.ReturnUrlParameter, returnUrl);
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
            AppendReturnUrl(_options.AccessDeniedPath, _options.ReturnUrlParameter, returnUrl);
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
        HttpCookieOptions cookieOptions = new(_options.Cookie);

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

    private string? ReadRequestCookie(string name)
    {
        foreach (HttpCookie cookie in _context.Request.Cookies)
        {
            if (string.Equals(cookie.Name, name, StringComparison.Ordinal))
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
            if (string.Equals(existing.Name, cookie.Name, StringComparison.Ordinal))
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
        => _context.GetEndpointMetadata<IApiEndpointMetadata>() is not null;

    private string CurrentRequestPath()
    {
        string path = _context.Request.Path.Value;
        return string.IsNullOrEmpty(path) ? "/" : path;
    }

    private static string AppendReturnUrl(string basePath, string parameterName, string returnUrl)
    {
        char separator = basePath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{basePath}{separator}{parameterName}={Uri.EscapeDataString(returnUrl)}";
    }

    private AuthenticateResult Cache(AuthenticateResult result)
    {
        _cachedResult = result;
        return result;
    }
}

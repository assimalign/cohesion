using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpAntiforgery"/> implementation. A stateless,
/// singleton-friendly service that operates on whatever
/// <see cref="IHttpContext"/> is passed: it reads/writes the cookie token via
/// the Cookies package, reads the request token from the configured form field
/// (Forms package) or header, and signs/verifies the pair through
/// <see cref="HttpAntiforgeryTokenEngine"/>.
/// </summary>
internal sealed class HttpAntiforgeryService : IHttpAntiforgery
{
    private static readonly HttpHeaderKey CacheControlHeader = "Cache-Control";
    private static readonly HttpHeaderKey PragmaHeader = "Pragma";
    private static readonly HttpHeaderKey XFrameOptionsHeader = "X-Frame-Options";

    private readonly HttpAntiforgeryOptions _options;
    private readonly HttpAntiforgeryTokenEngine _engine;

    public HttpAntiforgeryService(HttpAntiforgeryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        // The pluggable protector seam supersedes the static Key. When no protector is
        // configured, fall back to the built-in single-process HMAC protector over Key so the
        // zero-config default is unchanged.
        IHttpAntiforgeryProtector protector = options.Protector ?? new HmacHttpAntiforgeryProtector(options.Key);
        _engine = new HttpAntiforgeryTokenEngine(protector);
    }

    /// <inheritdoc />
    public HttpAntiforgeryTokenSet GetTokens(IHttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        CookieTokenResolution cookie = ResolveCookieToken(httpContext);
        string requestToken = _engine.GenerateRequestToken(cookie.Secret);

        return new HttpAntiforgeryTokenSet(requestToken, cookie.Token, _options.FormFieldName, _options.HeaderName);
    }

    /// <inheritdoc />
    public HttpAntiforgeryTokenSet GetAndStoreTokens(IHttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        CookieTokenResolution cookie = ResolveCookieToken(httpContext);
        if (cookie.IsNew)
        {
            StoreCookieToken(httpContext, cookie.Token);
        }

        ApplyNoCacheHeaders(httpContext);
        string requestToken = _engine.GenerateRequestToken(cookie.Secret);

        return new HttpAntiforgeryTokenSet(requestToken, cookie.Token, _options.FormFieldName, _options.HeaderName);
    }

    /// <inheritdoc />
    public void SetCookieTokenAndHeader(IHttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        CookieTokenResolution cookie = ResolveCookieToken(httpContext);
        if (cookie.IsNew)
        {
            StoreCookieToken(httpContext, cookie.Token);
        }

        ApplyNoCacheHeaders(httpContext);
    }

    /// <inheritdoc />
    public Task<bool> IsRequestValidAsync(IHttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // RFC 9110 §9.2.1 — safe methods are not state-changing and are not
        // validated.
        if (IsSafeMethod(httpContext.Request.Method))
        {
            return Task.FromResult(true);
        }

        byte[]? secret = _engine.ValidateCookieToken(ReadRequestCookieToken(httpContext));
        if (secret is null)
        {
            return Task.FromResult(false);
        }

        bool valid = _engine.ValidateRequestToken(ReadRequestToken(httpContext), secret);
        return Task.FromResult(valid);
    }

    /// <inheritdoc />
    public async Task ValidateRequestAsync(IHttpContext httpContext)
    {
        if (!await IsRequestValidAsync(httpContext).ConfigureAwait(false))
        {
            throw new AntiforgeryValidationException(
                "The required antiforgery token was missing or invalid.");
        }
    }

    private CookieTokenResolution ResolveCookieToken(IHttpContext httpContext)
    {
        string? existing = ReadRequestCookieToken(httpContext);
        byte[]? secret = _engine.ValidateCookieToken(existing);

        if (secret is not null && existing is not null)
        {
            return new CookieTokenResolution(existing, secret, isNew: false);
        }

        string token = _engine.GenerateCookieToken(out byte[] freshSecret);
        return new CookieTokenResolution(token, freshSecret, isNew: true);
    }

    private string? ReadRequestCookieToken(IHttpContext httpContext)
    {
        foreach (HttpCookie cookie in httpContext.Request.Cookies)
        {
            if (string.Equals(cookie.Name, _options.CookieName, StringComparison.Ordinal))
            {
                return cookie.Value;
            }
        }

        return null;
    }

    private string? ReadRequestToken(IHttpContext httpContext)
    {
        // Prefer the form field (classic <form> posts); the form is only
        // populated when the body has already been parsed by the Forms layer.
        IHttpFormCollection form = httpContext.Request.Form;
        if (form.Count > 0
            && form.TryGetValue(_options.FormFieldName, out HttpQueryValue formValue)
            && !string.IsNullOrEmpty(formValue.Value))
        {
            return formValue.Value;
        }

        // Fall back to the header (AJAX/SPA clients).
        if (httpContext.Request.Headers.TryGetValue(_options.HeaderName, out HttpHeaderValue headerValue)
            && !string.IsNullOrEmpty(headerValue.Value))
        {
            return headerValue.Value;
        }

        return null;
    }

    private void StoreCookieToken(IHttpContext httpContext, string token)
    {
        IHttpCookieCollection cookies = httpContext.Response.Cookies;
        RemoveByName(cookies, _options.CookieName);

        cookies.Add(new HttpCookie(_options.CookieName, token, new HttpCookieOptions
        {
            HttpOnly = _options.CookieHttpOnly,
            Secure = _options.CookieSecure,
            SameSite = _options.CookieSameSite,
            Path = _options.CookiePath,
        }));
    }

    private static void ApplyNoCacheHeaders(IHttpContext httpContext)
    {
        IHttpHeaderCollection headers = httpContext.Response.Headers;

        // A page carrying an antiforgery token must not be cached, or a shared
        // cache could serve one user's token to another.
        headers[CacheControlHeader] = "no-cache, no-store";
        headers[PragmaHeader] = "no-cache";
        headers[XFrameOptionsHeader] = "SAMEORIGIN";
    }

    private static void RemoveByName(IHttpCookieCollection cookies, string name)
    {
        HttpCookie? existing = null;
        foreach (HttpCookie cookie in cookies)
        {
            if (string.Equals(cookie.Name, name, StringComparison.Ordinal))
            {
                existing = cookie;
                break;
            }
        }

        if (existing is not null)
        {
            cookies.Remove(existing);
        }
    }

    private static bool IsSafeMethod(HttpMethod method)
    {
        return method == HttpMethod.Get
            || method == HttpMethod.Head
            || method == HttpMethod.Options
            || method == HttpMethod.Trace;
    }

    private readonly struct CookieTokenResolution
    {
        public CookieTokenResolution(string token, byte[] secret, bool isNew)
        {
            Token = token;
            Secret = secret;
            IsNew = isNew;
        }

        public string Token { get; }

        public byte[] Secret { get; }

        public bool IsNew { get; }
    }
}

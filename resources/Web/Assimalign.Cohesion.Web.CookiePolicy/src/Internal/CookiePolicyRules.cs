using System;
using System.Diagnostics.CodeAnalysis;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.CookiePolicy.Internal;

/// <summary>
/// The validated, immutable form of <see cref="CookiePolicyOptions"/>, captured once at registration,
/// and the rule evaluation every appended cookie goes through.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TryApply"/> is the single decision point. It never mutates the cookie it is given: a
/// cookie whose attributes must change is copied (the model's own copy-on-write convention, as
/// <see cref="HttpCookie.ClampLifetime(DateTimeOffset, TimeSpan)"/> does), so an application that reuses
/// one <see cref="HttpCookie"/> instance across requests never sees one request's decision leak into
/// another.
/// </para>
/// <para>
/// The prefix checks follow the user agent, not the server section of RFC 6265bis: a server is told
/// to match <c>__Secure-</c> and <c>__Host-</c> case-sensitively (&#167; 4.1.3), but a user agent MUST
/// match them case-insensitively (&#167; 5.4) and ignores a cookie that breaks one (&#167; 5.7). The
/// policy exists to keep the cookies it emits acceptable to the user agent, so it matches the way the
/// user agent does.
/// </para>
/// </remarks>
internal sealed class CookiePolicyRules
{
    private const string securePrefix = "__Secure-";
    private const string hostPrefix = "__Host-";

    private readonly HttpCookieSameSiteMode _minimumSameSite;
    private readonly CookieSecurePolicy _secure;
    private readonly CookieHttpOnlyPolicy _httpOnly;
    private readonly CookieViolationAction _sameSiteNoneWithoutSecure;
    private readonly CookieViolationAction _prefixViolation;
    private readonly TimeSpan _maxLifetime;
    private readonly TimeProvider _timeProvider;
    private readonly Func<IHttpContext, bool>? _checkConsentNeeded;
    private readonly Action<CookiePolicyRejectionContext>? _onRejected;
    private readonly string _consentCookieName;
    private readonly string _consentCookieValue;
    private readonly HttpCookieOptions _consentCookie;

    private CookiePolicyRules(CookiePolicyOptions options, HttpCookieOptions consentCookie)
    {
        _minimumSameSite = options.MinimumSameSitePolicy;
        _secure = options.Secure;
        _httpOnly = options.HttpOnly;
        _sameSiteNoneWithoutSecure = options.SameSiteNoneWithoutSecure;
        _prefixViolation = options.PrefixViolation;
        _maxLifetime = options.MaxLifetime;
        _timeProvider = options.TimeProvider;
        _checkConsentNeeded = options.CheckConsentNeeded;
        _onRejected = options.OnRejected;
        _consentCookieName = options.ConsentCookieName;
        _consentCookieValue = options.ConsentCookieValue;
        _consentCookie = consentCookie;
    }

    /// <summary>
    /// Validates <paramref name="options"/> and captures them.
    /// </summary>
    /// <param name="options">The configured options.</param>
    /// <param name="paramName">The parameter name reported by a validation failure.</param>
    /// <returns>The captured policy.</returns>
    /// <exception cref="ArgumentException">An option is out of range or the consent cookie is malformed.</exception>
    public static CookiePolicyRules Create(CookiePolicyOptions options, string paramName)
    {
        if (!Enum.IsDefined(options.MinimumSameSitePolicy))
        {
            throw new ArgumentException(
                $"'{options.MinimumSameSitePolicy}' is not a defined SameSite mode for the minimum SameSite policy.",
                paramName);
        }

        if (!Enum.IsDefined(options.Secure))
        {
            throw new ArgumentException($"'{options.Secure}' is not a defined Secure policy.", paramName);
        }

        if (!Enum.IsDefined(options.HttpOnly))
        {
            throw new ArgumentException($"'{options.HttpOnly}' is not a defined HttpOnly policy.", paramName);
        }

        if (!Enum.IsDefined(options.SameSiteNoneWithoutSecure))
        {
            throw new ArgumentException(
                $"'{options.SameSiteNoneWithoutSecure}' is not a defined action for SameSite=None without Secure.",
                paramName);
        }

        if (!Enum.IsDefined(options.PrefixViolation))
        {
            throw new ArgumentException(
                $"'{options.PrefixViolation}' is not a defined action for a cookie prefix violation.",
                paramName);
        }

        if (options.MaxLifetime <= TimeSpan.Zero || options.MaxLifetime > HttpCookieLimits.DefaultMaxLifetime)
        {
            throw new ArgumentException(
                $"The maximum cookie lifetime must be greater than zero and at most {HttpCookieLimits.DefaultMaxLifetime.TotalDays} days, " +
                $"the limit a user agent enforces (RFC 6265bis 5.5); got {options.MaxLifetime}.",
                paramName);
        }

        if (options.TimeProvider is null)
        {
            throw new ArgumentException("The cookie policy's time provider must not be null.", paramName);
        }

        if (string.IsNullOrEmpty(options.ConsentCookieValue))
        {
            throw new ArgumentException(
                "The consent cookie value must not be empty: an empty value cannot be told apart from a deleted consent cookie.",
                paramName);
        }

        if (options.ConsentCookie.MaxAge is TimeSpan maxAge && maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"The consent cookie's Max-Age must be positive when set; got {maxAge}. A zero or negative Max-Age would delete the consent it records.",
                paramName);
        }

        if (options.ConsentCookie.Expires is not null)
        {
            throw new ArgumentException(
                "The consent cookie must not set Expires: one fixed date would be stamped on every grant. Set its lifetime with Max-Age.",
                paramName);
        }

        // The consent cookie is the record of consent, so it must be issuable without consent.
        HttpCookieOptions consentCookie = new(options.ConsentCookie)
        {
            IsEssential = true,
        };

        try
        {
            // HttpCookie validates the name and value against the RFC 6265 grammar; do it here, at
            // registration, rather than the first time an application grants consent.
            _ = new HttpCookie(options.ConsentCookieName, options.ConsentCookieValue, consentCookie);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "The consent cookie name or value is not a valid RFC 6265 cookie name or value.",
                paramName,
                exception);
        }

        return new CookiePolicyRules(options, consentCookie);
    }

    /// <summary>
    /// Gets the name of the cookie that records consent.
    /// </summary>
    public string ConsentCookieName => _consentCookieName;

    /// <summary>
    /// Evaluates the consent predicate for <paramref name="context"/>.
    /// </summary>
    public bool IsConsentNeeded(IHttpContext context) => _checkConsentNeeded?.Invoke(context) ?? false;

    /// <summary>
    /// Reads whether the request carries the consent cookie with the configured value. When the request
    /// carries the name more than once, the first occurrence decides, as a user agent sends the most
    /// specific cookie first (RFC 6265bis &#167; 5.8.3).
    /// </summary>
    public bool HasConsentCookie(IHttpContext context)
    {
        foreach (HttpCookie cookie in context.Request.Cookies)
        {
            if (string.Equals(cookie.Name, _consentCookieName, StringComparison.Ordinal))
            {
                return string.Equals(cookie.Value, _consentCookieValue, StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>
    /// Creates the cookie that records consent.
    /// </summary>
    public HttpCookie CreateConsentCookie() => new(_consentCookieName, _consentCookieValue, _consentCookie);

    /// <summary>
    /// Creates the deletion of the consent cookie: the same name, path, and domain, an empty value, an
    /// epoch <c>Expires</c>, and a zero <c>Max-Age</c>.
    /// </summary>
    public HttpCookie CreateConsentDeletionCookie()
    {
        HttpCookieOptions options = new(_consentCookie)
        {
            Expires = DateTimeOffset.UnixEpoch,
            MaxAge = TimeSpan.Zero,
        };

        return new HttpCookie(_consentCookieName, string.Empty, options);
    }

    /// <summary>
    /// Judges a cookie the application appended. Returns <see langword="true"/> with the cookie to queue
    /// in <paramref name="issued"/>, which is <paramref name="cookie"/> itself when nothing changed, or
    /// <see langword="false"/> when the cookie is dropped, after reporting it to the rejection hook.
    /// </summary>
    /// <param name="context">The exchange the cookie belongs to.</param>
    /// <param name="consent">The exchange's consent feature: this policy's own, or the one installed before it.</param>
    /// <param name="cookie">The cookie as the application appended it.</param>
    /// <param name="issued">The cookie to queue, when the cookie is kept.</param>
    /// <returns><see langword="true"/> when the cookie is kept; <see langword="false"/> when it is dropped.</returns>
    public bool TryApply(
        IHttpContext context,
        ICookieConsentFeature consent,
        HttpCookie cookie,
        [NotNullWhen(true)] out HttpCookie? issued)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        HttpCookieOptions appended = cookie.Options;
        HttpCookieOptions? rewritten = null;

        // 1. Consent. It goes first because it is the cheapest rejection. An essential cookie never needs
        //    consent, and neither does a deletion: removing a cookie from the client is always allowed.
        if (!appended.IsEssential && !IsDeletion(appended, now) && !consent.CanTrack)
        {
            return Reject(context, cookie, CookiePolicyRejectionReason.ConsentRequired, out issued);
        }

        // 2. The floors. Each one only adds protection.
        if (!appended.Secure && IsSecureFloorRequired(context))
        {
            Writable().Secure = true;
        }

        if (_httpOnly == CookieHttpOnlyPolicy.Always && !appended.HttpOnly)
        {
            Writable().HttpOnly = true;
        }

        if (Rank(appended.SameSite) < Rank(_minimumSameSite))
        {
            Writable().SameSite = _minimumSameSite;
        }

        // 3a. A user agent ignores SameSite=None without Secure (RFC 6265bis 5.7, step 19). Evaluated after
        //     the floors, so a Secure floor or a raised SameSite has already satisfied it where it applies.
        HttpCookieOptions current = rewritten ?? appended;
        if (current.SameSite == HttpCookieSameSiteMode.None && !current.Secure)
        {
            if (_sameSiteNoneWithoutSecure == CookieViolationAction.Reject)
            {
                return Reject(context, cookie, CookiePolicyRejectionReason.SameSiteNoneWithoutSecure, out issued);
            }

            Writable().Secure = true;
        }

        // 3b. The name prefixes (RFC 6265bis 4.1.3), matched case-insensitively as the user agent does
        //     (5.4) because it ignores a cookie that breaks one (5.7, steps 20 and 21).
        current = rewritten ?? appended;
        if (cookie.Name.StartsWith(hostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsHostPrefixCompliant(current))
            {
                if (_prefixViolation == CookieViolationAction.Reject)
                {
                    return Reject(context, cookie, CookiePolicyRejectionReason.HostPrefixViolation, out issued);
                }

                HttpCookieOptions repaired = Writable();
                repaired.Secure = true;
                repaired.Path = "/";
                repaired.Domain = null;
            }
        }
        else if (cookie.Name.StartsWith(securePrefix, StringComparison.OrdinalIgnoreCase) && !current.Secure)
        {
            if (_prefixViolation == CookieViolationAction.Reject)
            {
                return Reject(context, cookie, CookiePolicyRejectionReason.SecurePrefixViolation, out issued);
            }

            Writable().Secure = true;
        }

        HttpCookie candidate = rewritten is null ? cookie : new HttpCookie(cookie.Name, cookie.Value, rewritten);

        // 4. The lifetime cap, applied at emission against the policy's clock (RFC 6265bis 5.5). The model
        //    owns the clamping math and leaves deletions untouched.
        issued = candidate.ClampLifetime(now, _maxLifetime);
        return true;

        HttpCookieOptions Writable() => rewritten ??= new HttpCookieOptions(appended);
    }

    private bool IsSecureFloorRequired(IHttpContext context) => _secure switch
    {
        CookieSecurePolicy.Always => true,
        CookieSecurePolicy.SameAsRequest => context.EffectiveScheme == HttpScheme.Https,
        _ => false,
    };

    private bool Reject(
        IHttpContext context,
        HttpCookie cookie,
        CookiePolicyRejectionReason reason,
        out HttpCookie? issued)
    {
        issued = null;
        _onRejected?.Invoke(new CookiePolicyRejectionContext(context, cookie, reason));
        return false;
    }

    /// <summary>
    /// Determines whether a cookie expires the moment a user agent stores it, the way a user agent reads
    /// it (RFC 6265bis 5.6.2 and 5.7): <c>Max-Age</c>, when present, wins over <c>Expires</c>, and a
    /// <c>Max-Age</c> that serializes to zero or less, or an <c>Expires</c> at or before now, expires the
    /// cookie at once.
    /// </summary>
    private static bool IsDeletion(HttpCookieOptions options, DateTimeOffset now)
    {
        if (options.MaxAge is TimeSpan maxAge)
        {
            // HttpCookie serializes Max-Age as whole seconds, so judge the value that reaches the wire.
            return (long)maxAge.TotalSeconds <= 0;
        }

        return options.Expires is DateTimeOffset expires && expires <= now;
    }

    /// <summary>
    /// A <c>__Host-</c> cookie must be <c>Secure</c>, carry <c>Path=/</c>, and carry no <c>Domain</c>
    /// (RFC 6265bis 4.1.3.2). <see cref="HttpCookie"/> omits a blank <c>Path</c> or <c>Domain</c> when it
    /// serializes, so a blank <c>Domain</c> is absent and a blank <c>Path</c> is missing.
    /// </summary>
    private static bool IsHostPrefixCompliant(HttpCookieOptions options)
        => options.Secure
            && string.Equals(options.Path, "/", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(options.Domain);

    /// <summary>
    /// Orders the <c>SameSite</c> modes from least to most restrictive, independent of the enum's numeric
    /// values. An undefined value ranks with <see cref="HttpCookieSameSiteMode.Unspecified"/>.
    /// </summary>
    private static int Rank(HttpCookieSameSiteMode mode) => mode switch
    {
        HttpCookieSameSiteMode.None => 1,
        HttpCookieSameSiteMode.Lax => 2,
        HttpCookieSameSiteMode.Strict => 3,
        _ => 0,
    };
}

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Internal;

/// <summary>
/// A <see cref="SecurityHeadersPolicy"/> compiled once, at builder time, into the field values it emits.
/// Applying it to a response is a presence check and an assignment per field; only a nonce-bearing
/// Content Security Policy does any per-response work (one string join).
/// </summary>
internal sealed class SecurityHeadersPlan
{
    private const string noSniff = "nosniff";
    private const string frameAncestorsDirective = "frame-ancestors";

    private readonly bool _contentTypeOptions;
    private readonly string? _xFrameOptions;
    private readonly ContentSecurityPolicyTemplate? _contentSecurityPolicy;
    private readonly ContentSecurityPolicyTemplate? _contentSecurityPolicyWithoutFraming;
    private readonly ContentSecurityPolicyTemplate? _contentSecurityPolicyReportOnly;
    private readonly string? _referrerPolicy;
    private readonly string? _crossOriginOpenerPolicy;
    private readonly string? _crossOriginEmbedderPolicy;
    private readonly string? _crossOriginResourcePolicy;
    private readonly string? _permissionsPolicy;
    private readonly bool _overwrite;

    private SecurityHeadersPlan(SecurityHeadersPolicy policy)
    {
        _contentTypeOptions = policy.ContentTypeOptions;
        _overwrite = policy.OverwriteExistingHeaders;

        // Enumerations are mapped first: an undefined value throws here, at composition.
        _referrerPolicy = policy.ReferrerPolicy is { } referrer ? SecurityHeadersTokens.ToToken(referrer) : null;
        _crossOriginOpenerPolicy = policy.CrossOriginOpenerPolicy is { } opener ? SecurityHeadersTokens.ToToken(opener) : null;
        _crossOriginEmbedderPolicy = policy.CrossOriginEmbedderPolicy is { } embedder ? SecurityHeadersTokens.ToToken(embedder) : null;
        _crossOriginResourcePolicy = policy.CrossOriginResourcePolicy is { } resource ? SecurityHeadersTokens.ToToken(resource) : null;
        _permissionsPolicy = policy.PermissionsPolicy?.ToString();

        string? frameAncestors = policy.Framing is { } framing
            ? string.Concat(frameAncestorsDirective, " ", framing.FrameAncestors)
            : null;
        _xFrameOptions = policy.Framing?.XFrameOptions;

        // Two enforced variants: the configured policy with the framing directive appended, and the
        // configured policy alone, used when the application has taken ownership of framing.
        if (policy.ContentSecurityPolicy is { } contentSecurityPolicy)
        {
            _contentSecurityPolicy = ContentSecurityPolicyTemplate.Create(contentSecurityPolicy.Directives, frameAncestors);
            _contentSecurityPolicyWithoutFraming = frameAncestors is null
                ? _contentSecurityPolicy
                : ContentSecurityPolicyTemplate.Create(contentSecurityPolicy.Directives, appendedDirective: null);
        }
        else if (frameAncestors is not null)
        {
            _contentSecurityPolicy = ContentSecurityPolicyTemplate.FromDirective(frameAncestors);
        }

        if (policy.ContentSecurityPolicyReportOnly is { } reportOnly)
        {
            _contentSecurityPolicyReportOnly = ContentSecurityPolicyTemplate.Create(reportOnly.Directives, appendedDirective: null);
        }
    }

    /// <summary>
    /// Compiles <paramref name="policy"/>.
    /// </summary>
    /// <exception cref="System.ArgumentOutOfRangeException">An enumeration property holds an undefined value.</exception>
    public static SecurityHeadersPlan Compile(SecurityHeadersPolicy policy) => new(policy);

    /// <summary>
    /// Stages the plan's fields on a response whose head has not been committed.
    /// </summary>
    /// <param name="headers">The response headers.</param>
    /// <param name="feature">The exchange's feature, read only when a policy carries a nonce source.</param>
    /// <exception cref="System.InvalidOperationException">The feature supplied a nonce that is not a CSP base64-value.</exception>
    public void ApplyTo(IHttpHeaderCollection headers, ISecurityHeadersFeature feature)
    {
        // The application owns framing when it set X-Frame-Options, or an enforced policy that addresses
        // framing: emitting the default beside either would contradict the application's choice (a
        // frame-ancestors 'none' would override its SAMEORIGIN in every current user agent).
        bool applicationOwnsFraming = !_overwrite
            && (headers.ContainsKey(HttpHeaderKey.XFrameOptions) || DeclaresFrameAncestors(headers));

        if (_contentTypeOptions)
        {
            Stage(headers, HttpHeaderKey.XContentTypeOptions, noSniff);
        }

        if (_xFrameOptions is not null && !applicationOwnsFraming)
        {
            Stage(headers, HttpHeaderKey.XFrameOptions, _xFrameOptions);
        }

        ContentSecurityPolicyTemplate? contentSecurityPolicy = applicationOwnsFraming
            ? _contentSecurityPolicyWithoutFraming
            : _contentSecurityPolicy;
        if (contentSecurityPolicy is not null && CanStage(headers, HttpHeaderKey.ContentSecurityPolicy))
        {
            headers[HttpHeaderKey.ContentSecurityPolicy] = contentSecurityPolicy.Render(feature);
        }

        if (_contentSecurityPolicyReportOnly is not null && CanStage(headers, HttpHeaderKey.ContentSecurityPolicyReportOnly))
        {
            headers[HttpHeaderKey.ContentSecurityPolicyReportOnly] = _contentSecurityPolicyReportOnly.Render(feature);
        }

        Stage(headers, HttpHeaderKey.ReferrerPolicy, _referrerPolicy);
        Stage(headers, HttpHeaderKey.CrossOriginOpenerPolicy, _crossOriginOpenerPolicy);
        Stage(headers, HttpHeaderKey.CrossOriginEmbedderPolicy, _crossOriginEmbedderPolicy);
        Stage(headers, HttpHeaderKey.CrossOriginResourcePolicy, _crossOriginResourcePolicy);
        Stage(headers, HttpHeaderKey.PermissionsPolicy, _permissionsPolicy);
    }

    private void Stage(IHttpHeaderCollection headers, HttpHeaderKey key, string? value)
    {
        if (value is not null && CanStage(headers, key))
        {
            headers[key] = value;
        }
    }

    private bool CanStage(IHttpHeaderCollection headers, HttpHeaderKey key) => _overwrite || !headers.ContainsKey(key);

    private static bool DeclaresFrameAncestors(IHttpHeaderCollection headers)
    {
        if (!headers.TryGetValue(HttpHeaderKey.ContentSecurityPolicy, out HttpHeaderValue policies))
        {
            return false;
        }

        for (int index = 0; index < policies.Count; index++)
        {
            if (policies[index] is { } policy && SecurityHeadersGrammar.DeclaresDirective(policy, frameAncestorsDirective))
            {
                return true;
            }
        }

        return false;
    }
}

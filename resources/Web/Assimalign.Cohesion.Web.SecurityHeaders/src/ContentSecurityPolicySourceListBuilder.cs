using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.SecurityHeaders.Internal;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// Builds the source list of one Content Security Policy directive (W3C Content Security Policy Level 3,
/// "serialized-source-list"): keyword sources are written with their quotes, scheme and host sources are
/// validated against the CSP grammar, and <see cref="Nonce"/> stands for the per-request nonce.
/// </summary>
/// <remarks>
/// <para>
/// An instance is handed to the configuration callback of a source-list directive on
/// <see cref="ContentSecurityPolicyBuilder"/> (for example
/// <c>csp.ScriptSrc(sources =&gt; sources.Self().Nonce())</c>). Sources serialize in the order they were
/// added, and adding the same source twice keeps the first.
/// </para>
/// <para>
/// A list must name at least one source, and <see cref="None"/> must be its only source: CSP ignores
/// <c>'none'</c> beside other sources, so the combination would not mean what it says. Both rules are
/// checked when the callback returns. Keywords that apply only to some directives (for example
/// <c>'strict-dynamic'</c> outside <c>script-src</c>) are serialized as given; user agents ignore them
/// where the specification does.
/// </para>
/// </remarks>
public sealed class ContentSecurityPolicySourceListBuilder
{
    private const string noneSource = "'none'";

    private readonly List<string?> _sources = new();
    private bool _hasNonce;

    internal ContentSecurityPolicySourceListBuilder()
    {
    }

    /// <summary>
    /// Adds <c>'none'</c>, which matches nothing. It must be the only source in the list.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public ContentSecurityPolicySourceListBuilder None() => Add(noneSource);

    /// <summary>
    /// Adds <c>'self'</c>, which matches the document's own origin (and its secure upgrade).
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public ContentSecurityPolicySourceListBuilder Self() => Add("'self'");

    /// <summary>
    /// Adds <c>'unsafe-inline'</c>, which allows inline scripts, styles and event handlers. A nonce or
    /// hash source in the same list makes user agents ignore it, which is how a nonce-based policy stays
    /// compatible with older user agents.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public ContentSecurityPolicySourceListBuilder UnsafeInline() => Add("'unsafe-inline'");

    /// <summary>
    /// Adds <c>'unsafe-eval'</c>, which allows <c>eval()</c> and similar string-to-code APIs.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public ContentSecurityPolicySourceListBuilder UnsafeEval() => Add("'unsafe-eval'");

    /// <summary>
    /// Adds <c>'unsafe-hashes'</c>, which lets hash sources match inline event-handler attributes and
    /// <c>style</c> attributes.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public ContentSecurityPolicySourceListBuilder UnsafeHashes() => Add("'unsafe-hashes'");

    /// <summary>
    /// Adds <c>'strict-dynamic'</c>, which extends the trust a nonce or hash grants a script to the
    /// scripts it loads, and makes user agents ignore host and scheme sources in the list.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public ContentSecurityPolicySourceListBuilder StrictDynamic() => Add("'strict-dynamic'");

    /// <summary>
    /// Adds <c>'report-sample'</c>, which asks user agents to include a sample of the violating code in
    /// violation reports.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public ContentSecurityPolicySourceListBuilder ReportSample() => Add("'report-sample'");

    /// <summary>
    /// Adds <c>'wasm-unsafe-eval'</c>, which allows WebAssembly compilation without allowing JavaScript
    /// <c>eval()</c>.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public ContentSecurityPolicySourceListBuilder WasmUnsafeEval() => Add("'wasm-unsafe-eval'");

    /// <summary>
    /// Adds the per-request nonce source, <c>'nonce-…'</c>. The middleware substitutes the exchange's
    /// <see cref="ISecurityHeadersFeature.Nonce"/> when it writes the header, so an element carrying that
    /// nonce in its <c>nonce</c> attribute is allowed.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public ContentSecurityPolicySourceListBuilder Nonce()
    {
        if (!_hasNonce)
        {
            _hasNonce = true;
            _sources.Add(null);
        }

        return this;
    }

    /// <summary>
    /// Adds a scheme source, such as <c>https:</c>, <c>data:</c> or <c>blob:</c>, which matches every URL
    /// with that scheme.
    /// </summary>
    /// <param name="scheme">The scheme, with or without its trailing colon (<c>https</c> or <c>https:</c>).</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scheme"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="scheme"/> is not an RFC 3986 scheme.</exception>
    public ContentSecurityPolicySourceListBuilder Scheme(string scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        ReadOnlySpan<char> name = scheme.EndsWith(':') ? scheme.AsSpan(0, scheme.Length - 1) : scheme.AsSpan();
        if (!SecurityHeadersGrammar.IsScheme(name))
        {
            throw new ArgumentException(
                $"'{scheme}' is not a scheme source; a scheme is ALPHA *( ALPHA / DIGIT / '+' / '-' / '.' ), optionally followed by ':'.",
                nameof(scheme));
        }

        return Add(string.Concat(name.ToString().ToLowerInvariant(), ":"));
    }

    /// <summary>
    /// Adds a host source, such as <c>https://cdn.example.com</c>, <c>*.example.com</c>,
    /// <c>example.com:8443</c> or <c>https://example.com/scripts/</c>.
    /// </summary>
    /// <param name="source">
    /// The CSP <c>host-source</c>: an optional <c>scheme://</c>, a host (<c>*</c>, or labels of letters,
    /// digits and hyphens with an optional <c>*.</c> wildcard prefix), an optional <c>:port</c> (digits or
    /// <c>*</c>), and an optional absolute path whose <c>,</c> and <c>;</c> are percent-encoded.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not a CSP host source.</exception>
    public ContentSecurityPolicySourceListBuilder Host(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!SecurityHeadersGrammar.IsHostSource(source))
        {
            throw new ArgumentException(
                $"'{source}' is not a CSP host source ([scheme://]host[:port][/path], no whitespace, ',' or ';'). " +
                "Use Scheme() for a scheme-only source and the keyword methods for quoted keywords.",
                nameof(source));
        }

        return Add(source);
    }

    /// <summary>
    /// Adds a hash source, <c>'&lt;algorithm&gt;-&lt;digest&gt;'</c>, which allows the inline script or
    /// style whose digest matches.
    /// </summary>
    /// <param name="algorithm">The digest algorithm.</param>
    /// <param name="base64Digest">The base64-encoded digest of the element's text.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="base64Digest"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="algorithm"/> is not a defined value.</exception>
    /// <exception cref="ArgumentException"><paramref name="base64Digest"/> is not a CSP <c>base64-value</c>.</exception>
    public ContentSecurityPolicySourceListBuilder Hash(ContentSecurityPolicyHashAlgorithm algorithm, string base64Digest)
    {
        ArgumentNullException.ThrowIfNull(base64Digest);

        string token = SecurityHeadersTokens.ToToken(algorithm);
        if (!SecurityHeadersGrammar.IsBase64Value(base64Digest))
        {
            throw new ArgumentException(
                $"'{base64Digest}' is not a base64 digest (ALPHA / DIGIT / '+' / '/' / '-' / '_', up to two '=' of padding).",
                nameof(base64Digest));
        }

        return Add(string.Concat("'", token, "-", base64Digest, "'"));
    }

    /// <summary>
    /// Returns the validated sources for <paramref name="directiveName"/>; a <see langword="null"/>
    /// element is the nonce source.
    /// </summary>
    internal string?[] Build(string directiveName)
    {
        if (_sources.Count == 0)
        {
            throw new ArgumentException(
                $"The {directiveName} source list is empty. Add a source, or call None() to match nothing.",
                "configure");
        }

        if (_sources.Count > 1 && _sources.Contains(noneSource))
        {
            throw new ArgumentException(
                $"The {directiveName} source list combines 'none' with other sources, and user agents ignore 'none' " +
                "beside them. Use None() alone, or remove it.",
                "configure");
        }

        return _sources.ToArray();
    }

    private ContentSecurityPolicySourceListBuilder Add(string source)
    {
        if (!_sources.Contains(source))
        {
            _sources.Add(source);
        }

        return this;
    }
}

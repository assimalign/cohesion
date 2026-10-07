using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Rewrite.Internal;

namespace Assimalign.Cohesion.Web.Rewrite;

/// <summary>
/// The rules <c>UseRewrite</c> evaluates, in the order they are added, and the bound on rule passes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Evaluation.</b> For every request the rules run in registration order, each against the URL as the
/// rules before it left it. A rewrite changes that URL and evaluation continues, unless the rule asks to
/// skip the remaining rules or to restart them (<see cref="RewriteFlow"/>). A redirect answers the request
/// and ends evaluation and the pipeline. Register redirects, the canonicalization helpers included, ahead
/// of the internal rewrites: a redirect built after a rewrite sends the client to the rewritten URL.
/// </para>
/// <para>
/// <b>Patterns.</b> A pattern given as a string is compiled as an interpreted, culture-invariant,
/// case-sensitive regular expression with a one-second match timeout. To use
/// <see cref="RegexOptions.NonBacktracking"/> (linear-time matching on attacker-controlled input) or a
/// source-generated <c>[GeneratedRegex]</c>, pass the <see cref="Regex"/> itself; its own options and timeout
/// apply. <see cref="RegexOptions.Compiled"/> is never used: under NativeAOT it falls back to the interpreter,
/// and a source-generated regex is the compiled-speed option.
/// </para>
/// <para>
/// <b>Targets.</b> A rewrite target is a path that starts with <c>/</c>, with an optional <c>?query</c>; a
/// redirect target may also carry a <c>#fragment</c>, or be an absolute <c>http</c> or <c>https</c> URL. A
/// target with no <c>?</c> keeps the request's query, a target with one replaces it, and a target ending in
/// <c>?</c> removes it. <c>$1</c> (or <c>${1}</c>) and <c>${name}</c> substitute capture groups, <c>$0</c> the
/// whole match, and <c>$$</c> is a literal <c>$</c>. Literal text is URL text, with characters a URL cannot
/// carry percent-encoded; a capture is percent-encoded for the part of the URL it lands in, so it can never
/// add a query parameter, a fragment or a host. Inside a <c>Map(path)</c> branch, a redirect's target path
/// is below the branch's prefix; an absolute URL is not.
/// </para>
/// <para>
/// Composition is dependency-free: every rule is built and validated while <c>UseRewrite</c> runs, and the
/// middleware keeps its own copy of the list, so a misconfigured rule fails at startup and changing the
/// options afterwards has no effect.
/// </para>
/// </remarks>
public sealed class RewriteOptions
{
    private static readonly TimeSpan _patternTimeout = TimeSpan.FromSeconds(1);

    private readonly List<IRewriteRule> _rules = new();
    private int _maxPasses = 10;

    /// <summary>
    /// Gets or sets the most rule passes one request may take. Defaults to <c>10</c>.
    /// </summary>
    /// <remarks>
    /// Every request takes one pass over the rules, and each restart (<see cref="RewriteFlow.Restart"/>)
    /// takes another. A request that would need more passes than this fails with an
    /// <see cref="InvalidOperationException"/>, which the exception boundary answers as a <c>500</c>: a rule
    /// set that keeps restarting loops, and looping is never the right answer.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than <c>1</c>.</exception>
    public int MaxPasses
    {
        get => _maxPasses;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxPasses = value;
        }
    }

    /// <summary>
    /// Gets the rules, in registration order.
    /// </summary>
    internal IReadOnlyList<IRewriteRule> Rules => _rules;

    /// <summary>
    /// Adds a rule.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rule"/> is <see langword="null"/>.</exception>
    public RewriteOptions Add(IRewriteRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        _rules.Add(rule);
        return this;
    }

    /// <summary>
    /// Adds a rule written as a delegate: it inspects the URL and acts through the
    /// <see cref="IRewriteContext"/>.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rule"/> is <see langword="null"/>.</exception>
    public RewriteOptions Add(Action<IRewriteContext> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return Add(new DelegateRule(rule));
    }

    /// <summary>
    /// Adds an internal rewrite: when <paramref name="pattern"/> matches, the URL becomes
    /// <paramref name="replacement"/> for the later rules and the rest of the pipeline. The client is not told.
    /// </summary>
    /// <param name="pattern">The regular expression, compiled interpreted with a one-second match timeout.</param>
    /// <param name="replacement">The target path, with an optional <c>?query</c>; <c>$1</c> and <c>${name}</c> substitute captures.</param>
    /// <param name="flow">What happens after the rewrite. Defaults to <see cref="RewriteFlow.Continue"/>.</param>
    /// <param name="target">What the pattern matches. Defaults to <see cref="RewriteMatchTarget.Path"/>.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pattern"/> is not a valid regular expression, or <paramref name="replacement"/> is empty,
    /// malformed, not a path, or references a group the pattern does not define.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="flow"/> or <paramref name="target"/> is not a defined value.</exception>
    public RewriteOptions AddRewrite(string pattern, string replacement, RewriteFlow flow = RewriteFlow.Continue, RewriteMatchTarget target = RewriteMatchTarget.Path)
    {
        return AddRewrite(CreatePattern(pattern), replacement, flow, target);
    }

    /// <summary>
    /// Adds an internal rewrite with a caller-supplied regular expression, such as a source-generated
    /// <c>[GeneratedRegex]</c> or one built with <see cref="RegexOptions.NonBacktracking"/>: when it matches,
    /// the URL becomes <paramref name="replacement"/> for the later rules and the rest of the pipeline.
    /// </summary>
    /// <param name="pattern">The regular expression; its own options and match timeout apply.</param>
    /// <param name="replacement">The target path, with an optional <c>?query</c>; <c>$1</c> and <c>${name}</c> substitute captures.</param>
    /// <param name="flow">What happens after the rewrite. Defaults to <see cref="RewriteFlow.Continue"/>.</param>
    /// <param name="target">What the pattern matches. Defaults to <see cref="RewriteMatchTarget.Path"/>.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="replacement"/> is empty, malformed, not a path, or references a group the pattern does
    /// not define.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="flow"/> or <paramref name="target"/> is not a defined value.</exception>
    public RewriteOptions AddRewrite(Regex pattern, string replacement, RewriteFlow flow = RewriteFlow.Continue, RewriteMatchTarget target = RewriteMatchTarget.Path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ValidateFlow(flow);
        ValidateTarget(target);

        return Add(PatternRule.CreateRewrite(pattern, replacement, flow, target));
    }

    /// <summary>
    /// Adds an internal rewrite decided by a predicate: when <paramref name="predicate"/> holds, the URL
    /// becomes <paramref name="replacement"/> for the later rules and the rest of the pipeline.
    /// </summary>
    /// <param name="predicate">Decides, per request, whether the rule applies.</param>
    /// <param name="replacement">The fixed target path, with an optional <c>?query</c>. It cannot substitute captures.</param>
    /// <param name="flow">What happens after the rewrite. Defaults to <see cref="RewriteFlow.Continue"/>.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="replacement"/> is empty, malformed, not a path, or substitutes a capture.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="flow"/> is not a defined value.</exception>
    public RewriteOptions AddRewrite(Func<IRewriteContext, bool> predicate, string replacement, RewriteFlow flow = RewriteFlow.Continue)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ValidateFlow(flow);

        return Add(PredicateRule.CreateRewrite(predicate, replacement, flow));
    }

    /// <summary>
    /// Adds a redirect: when <paramref name="pattern"/> matches the path, the client is answered with
    /// <c>302 Found</c> and a <c>Location</c> of <paramref name="replacement"/>, and the pipeline ends.
    /// </summary>
    /// <param name="pattern">The regular expression, compiled interpreted with a one-second match timeout.</param>
    /// <param name="replacement">
    /// The target: a path with an optional <c>?query</c> and <c>#fragment</c>, or an absolute <c>http</c> or
    /// <c>https</c> URL; <c>$1</c> and <c>${name}</c> substitute captures.
    /// </param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pattern"/> is not a valid regular expression, or <paramref name="replacement"/> is empty,
    /// malformed, or references a group the pattern does not define.
    /// </exception>
    public RewriteOptions AddRedirect(string pattern, string replacement)
    {
        return AddRedirect(pattern, replacement, HttpStatusCode.Found);
    }

    /// <summary>
    /// Adds a redirect: when <paramref name="pattern"/> matches, the client is answered with
    /// <paramref name="statusCode"/> and a <c>Location</c> of <paramref name="replacement"/>, and the pipeline ends.
    /// </summary>
    /// <param name="pattern">The regular expression, compiled interpreted with a one-second match timeout.</param>
    /// <param name="replacement">
    /// The target: a path with an optional <c>?query</c> and <c>#fragment</c>, or an absolute <c>http</c> or
    /// <c>https</c> URL; <c>$1</c> and <c>${name}</c> substitute captures.
    /// </param>
    /// <param name="statusCode">
    /// The status: <c>301</c> or <c>308</c> for a permanent move, <c>302</c> or <c>307</c> for a temporary one.
    /// <c>307</c> and <c>308</c> keep the request method and body (RFC 9110 §15.4).
    /// </param>
    /// <param name="target">What the pattern matches. Defaults to <see cref="RewriteMatchTarget.Path"/>.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pattern"/> is not a valid regular expression, or <paramref name="replacement"/> is empty,
    /// malformed, or references a group the pattern does not define.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="statusCode"/> is not <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>, or
    /// <paramref name="target"/> is not a defined value.
    /// </exception>
    public RewriteOptions AddRedirect(string pattern, string replacement, HttpStatusCode statusCode, RewriteMatchTarget target = RewriteMatchTarget.Path)
    {
        return AddRedirect(CreatePattern(pattern), replacement, statusCode, target);
    }

    /// <summary>
    /// Adds a redirect with a caller-supplied regular expression: when it matches the path, the client is
    /// answered with <c>302 Found</c> and a <c>Location</c> of <paramref name="replacement"/>, and the pipeline ends.
    /// </summary>
    /// <param name="pattern">The regular expression; its own options and match timeout apply.</param>
    /// <param name="replacement">
    /// The target: a path with an optional <c>?query</c> and <c>#fragment</c>, or an absolute <c>http</c> or
    /// <c>https</c> URL; <c>$1</c> and <c>${name}</c> substitute captures.
    /// </param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="replacement"/> is empty, malformed, or references a group the pattern does not define.
    /// </exception>
    public RewriteOptions AddRedirect(Regex pattern, string replacement)
    {
        return AddRedirect(pattern, replacement, HttpStatusCode.Found);
    }

    /// <summary>
    /// Adds a redirect with a caller-supplied regular expression: when it matches, the client is answered with
    /// <paramref name="statusCode"/> and a <c>Location</c> of <paramref name="replacement"/>, and the pipeline ends.
    /// </summary>
    /// <param name="pattern">The regular expression; its own options and match timeout apply.</param>
    /// <param name="replacement">
    /// The target: a path with an optional <c>?query</c> and <c>#fragment</c>, or an absolute <c>http</c> or
    /// <c>https</c> URL; <c>$1</c> and <c>${name}</c> substitute captures.
    /// </param>
    /// <param name="statusCode">
    /// The status: <c>301</c> or <c>308</c> for a permanent move, <c>302</c> or <c>307</c> for a temporary one.
    /// <c>307</c> and <c>308</c> keep the request method and body (RFC 9110 §15.4).
    /// </param>
    /// <param name="target">What the pattern matches. Defaults to <see cref="RewriteMatchTarget.Path"/>.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="replacement"/> is empty, malformed, or references a group the pattern does not define.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="statusCode"/> is not <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>, or
    /// <paramref name="target"/> is not a defined value.
    /// </exception>
    public RewriteOptions AddRedirect(Regex pattern, string replacement, HttpStatusCode statusCode, RewriteMatchTarget target = RewriteMatchTarget.Path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        RewriteStatusCodes.ValidateRedirect(statusCode, nameof(statusCode));
        ValidateTarget(target);

        return Add(PatternRule.CreateRedirect(pattern, replacement, statusCode, target));
    }

    /// <summary>
    /// Adds a redirect decided by a predicate: when <paramref name="predicate"/> holds, the client is answered
    /// with <c>302 Found</c> and a <c>Location</c> of <paramref name="replacement"/>, and the pipeline ends.
    /// </summary>
    /// <param name="predicate">Decides, per request, whether the rule applies.</param>
    /// <param name="replacement">
    /// The fixed target: a path with an optional <c>?query</c> and <c>#fragment</c>, or an absolute <c>http</c>
    /// or <c>https</c> URL. It cannot substitute captures.
    /// </param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="replacement"/> is empty, malformed, or substitutes a capture.</exception>
    public RewriteOptions AddRedirect(Func<IRewriteContext, bool> predicate, string replacement)
    {
        return AddRedirect(predicate, replacement, HttpStatusCode.Found);
    }

    /// <summary>
    /// Adds a redirect decided by a predicate: when <paramref name="predicate"/> holds, the client is answered
    /// with <paramref name="statusCode"/> and a <c>Location</c> of <paramref name="replacement"/>, and the
    /// pipeline ends.
    /// </summary>
    /// <param name="predicate">Decides, per request, whether the rule applies.</param>
    /// <param name="replacement">
    /// The fixed target: a path with an optional <c>?query</c> and <c>#fragment</c>, or an absolute <c>http</c>
    /// or <c>https</c> URL. It cannot substitute captures.
    /// </param>
    /// <param name="statusCode">The status: <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="replacement"/> is empty, malformed, or substitutes a capture.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</exception>
    public RewriteOptions AddRedirect(Func<IRewriteContext, bool> predicate, string replacement, HttpStatusCode statusCode)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        RewriteStatusCodes.ValidateRedirect(statusCode, nameof(statusCode));

        return Add(PredicateRule.CreateRedirect(predicate, replacement, statusCode));
    }

    /// <summary>
    /// Adds a canonicalization redirect to HTTPS: a request whose effective scheme is not <c>https</c> is
    /// answered with <c>308 Permanent Redirect</c> to the same host, path and query over <c>https</c> on port
    /// <c>443</c>.
    /// </summary>
    /// <remarks>
    /// The scheme and host are the effective ones: behind a TLS-terminating proxy that
    /// <c>UseForwardedHeaders</c> trusts, the proxy's forwarded <c>https</c> counts as secure, so the rule never
    /// redirects in a loop, and the <c>Location</c> names the host the client addressed.
    /// </remarks>
    /// <returns>The same options instance, for chaining.</returns>
    public RewriteOptions AddRedirectToHttps()
    {
        return AddRedirectToHttps(HttpStatusCode.PermanentRedirect);
    }

    /// <summary>
    /// Adds a canonicalization redirect to HTTPS: a request whose effective scheme is not <c>https</c> is
    /// answered with <paramref name="statusCode"/> to the same host, path and query over <c>https</c> on
    /// <paramref name="httpsPort"/>.
    /// </summary>
    /// <remarks>
    /// The scheme and host are the effective ones: behind a TLS-terminating proxy that
    /// <c>UseForwardedHeaders</c> trusts, the proxy's forwarded <c>https</c> counts as secure, so the rule never
    /// redirects in a loop, and the <c>Location</c> names the host the client addressed.
    /// </remarks>
    /// <param name="statusCode">The status: <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>. <c>307</c> and <c>308</c> keep the request method and body.</param>
    /// <param name="httpsPort">The port clients reach HTTPS on. The default <c>443</c> is left out of the <c>Location</c>.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="statusCode"/> is not <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>, or
    /// <paramref name="httpsPort"/> is outside 1–65535.
    /// </exception>
    public RewriteOptions AddRedirectToHttps(HttpStatusCode statusCode, int httpsPort = 443)
    {
        RewriteStatusCodes.ValidateRedirect(statusCode, nameof(statusCode));
        ArgumentOutOfRangeException.ThrowIfLessThan(httpsPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(httpsPort, 65535);

        return Add(new HttpsRedirectRule(statusCode, httpsPort));
    }

    /// <summary>
    /// Adds a canonicalization redirect to the <c>www.</c> host: a request for a host without the prefix is
    /// answered with <c>308 Permanent Redirect</c> to the same URL on <c>www.</c> plus that host.
    /// </summary>
    /// <remarks>
    /// The host is the effective one (behind a proxy that <c>UseForwardedHeaders</c> trusts, the host the client
    /// addressed); its scheme and port are kept. <c>localhost</c>, <c>*.localhost</c> and IP literals are never
    /// redirected.
    /// </remarks>
    /// <param name="domains">The request hosts the rule applies to, for example <c>example.com</c>; with none, every host.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="domains"/> holds a <see langword="null"/> or empty entry.</exception>
    public RewriteOptions AddRedirectToWww(params string[] domains)
    {
        return AddRedirectToWww(HttpStatusCode.PermanentRedirect, domains);
    }

    /// <summary>
    /// Adds a canonicalization redirect to the <c>www.</c> host: a request for a host without the prefix is
    /// answered with <paramref name="statusCode"/> to the same URL on <c>www.</c> plus that host.
    /// </summary>
    /// <remarks>
    /// The host is the effective one (behind a proxy that <c>UseForwardedHeaders</c> trusts, the host the client
    /// addressed); its scheme and port are kept. <c>localhost</c>, <c>*.localhost</c> and IP literals are never
    /// redirected.
    /// </remarks>
    /// <param name="statusCode">The status: <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</param>
    /// <param name="domains">The request hosts the rule applies to, for example <c>example.com</c>; with none, every host.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="domains"/> holds a <see langword="null"/> or empty entry.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</exception>
    public RewriteOptions AddRedirectToWww(HttpStatusCode statusCode, params string[] domains)
    {
        RewriteStatusCodes.ValidateRedirect(statusCode, nameof(statusCode));

        return Add(new HostRedirectRule(toWww: true, statusCode, CopyDomains(domains)));
    }

    /// <summary>
    /// Adds a canonicalization redirect away from the <c>www.</c> host: a request for a host with the prefix is
    /// answered with <c>308 Permanent Redirect</c> to the same URL on the host without it.
    /// </summary>
    /// <remarks>
    /// The host is the effective one (behind a proxy that <c>UseForwardedHeaders</c> trusts, the host the client
    /// addressed); its scheme and port are kept.
    /// </remarks>
    /// <param name="domains">The request hosts the rule applies to, for example <c>www.example.com</c>; with none, every host.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="domains"/> holds a <see langword="null"/> or empty entry.</exception>
    public RewriteOptions AddRedirectToNonWww(params string[] domains)
    {
        return AddRedirectToNonWww(HttpStatusCode.PermanentRedirect, domains);
    }

    /// <summary>
    /// Adds a canonicalization redirect away from the <c>www.</c> host: a request for a host with the prefix is
    /// answered with <paramref name="statusCode"/> to the same URL on the host without it.
    /// </summary>
    /// <remarks>
    /// The host is the effective one (behind a proxy that <c>UseForwardedHeaders</c> trusts, the host the client
    /// addressed); its scheme and port are kept.
    /// </remarks>
    /// <param name="statusCode">The status: <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</param>
    /// <param name="domains">The request hosts the rule applies to, for example <c>www.example.com</c>; with none, every host.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="domains"/> holds a <see langword="null"/> or empty entry.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</exception>
    public RewriteOptions AddRedirectToNonWww(HttpStatusCode statusCode, params string[] domains)
    {
        RewriteStatusCodes.ValidateRedirect(statusCode, nameof(statusCode));

        return Add(new HostRedirectRule(toWww: false, statusCode, CopyDomains(domains)));
    }

    /// <summary>
    /// Adds a canonicalization redirect to the trailing-slash form of the path: <c>/docs</c> is answered with
    /// <c>308 Permanent Redirect</c> to <c>/docs/</c>, keeping the query.
    /// </summary>
    /// <remarks>
    /// The root is never redirected, nor is a path whose last segment contains a <c>.</c>, which names a file
    /// (<c>/app.js</c>). Inside a <c>Map(path)</c> branch the root of the branch is not redirected either.
    /// </remarks>
    /// <returns>The same options instance, for chaining.</returns>
    public RewriteOptions AddRedirectToTrailingSlash()
    {
        return AddRedirectToTrailingSlash(HttpStatusCode.PermanentRedirect);
    }

    /// <summary>
    /// Adds a canonicalization redirect to the trailing-slash form of the path: <c>/docs</c> is answered with
    /// <paramref name="statusCode"/> to <c>/docs/</c>, keeping the query.
    /// </summary>
    /// <remarks>
    /// The root is never redirected, nor is a path whose last segment contains a <c>.</c>, which names a file
    /// (<c>/app.js</c>). Inside a <c>Map(path)</c> branch the root of the branch is not redirected either.
    /// </remarks>
    /// <param name="statusCode">The status: <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</exception>
    public RewriteOptions AddRedirectToTrailingSlash(HttpStatusCode statusCode)
    {
        RewriteStatusCodes.ValidateRedirect(statusCode, nameof(statusCode));

        return Add(new TrailingSlashRedirectRule(append: true, statusCode));
    }

    /// <summary>
    /// Adds a canonicalization redirect to the form of the path without a trailing slash: <c>/docs/</c> is
    /// answered with <c>308 Permanent Redirect</c> to <c>/docs</c>, keeping the query.
    /// </summary>
    /// <remarks>
    /// The root is never redirected, and inside a <c>Map(path)</c> branch neither is the root of the branch.
    /// Every trailing slash is removed, so <c>/docs//</c> takes one hop.
    /// </remarks>
    /// <returns>The same options instance, for chaining.</returns>
    public RewriteOptions AddRedirectToNoTrailingSlash()
    {
        return AddRedirectToNoTrailingSlash(HttpStatusCode.PermanentRedirect);
    }

    /// <summary>
    /// Adds a canonicalization redirect to the form of the path without a trailing slash: <c>/docs/</c> is
    /// answered with <paramref name="statusCode"/> to <c>/docs</c>, keeping the query.
    /// </summary>
    /// <remarks>
    /// The root is never redirected, and inside a <c>Map(path)</c> branch neither is the root of the branch.
    /// Every trailing slash is removed, so <c>/docs//</c> takes one hop.
    /// </remarks>
    /// <param name="statusCode">The status: <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</exception>
    public RewriteOptions AddRedirectToNoTrailingSlash(HttpStatusCode statusCode)
    {
        RewriteStatusCodes.ValidateRedirect(statusCode, nameof(statusCode));

        return Add(new TrailingSlashRedirectRule(append: false, statusCode));
    }

    /// <summary>
    /// Adds a canonicalization redirect to the lowercase form of the path: <c>/Docs/Intro</c> is answered with
    /// <c>308 Permanent Redirect</c> to <c>/docs/intro</c>. The query keeps its case.
    /// </summary>
    /// <remarks>
    /// Lowercasing uses the invariant culture. Inside a <c>Map(path)</c> branch only the path below the
    /// branch's prefix is lowercased.
    /// </remarks>
    /// <returns>The same options instance, for chaining.</returns>
    public RewriteOptions AddRedirectToLowercase()
    {
        return AddRedirectToLowercase(HttpStatusCode.PermanentRedirect);
    }

    /// <summary>
    /// Adds a canonicalization redirect to the lowercase form of the path: <c>/Docs/Intro</c> is answered with
    /// <paramref name="statusCode"/> to <c>/docs/intro</c>. The query keeps its case.
    /// </summary>
    /// <remarks>
    /// Lowercasing uses the invariant culture. Inside a <c>Map(path)</c> branch only the path below the
    /// branch's prefix is lowercased.
    /// </remarks>
    /// <param name="statusCode">The status: <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> is not <c>301</c>, <c>302</c>, <c>307</c> or <c>308</c>.</exception>
    public RewriteOptions AddRedirectToLowercase(HttpStatusCode statusCode)
    {
        RewriteStatusCodes.ValidateRedirect(statusCode, nameof(statusCode));

        return Add(new LowercaseRedirectRule(statusCode));
    }

    // Interpreted (never RegexOptions.Compiled, which needs dynamic code), culture-invariant and bounded by a
    // match timeout: the input is the client's URL.
    private static Regex CreatePattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        return new Regex(pattern, RegexOptions.CultureInvariant, _patternTimeout);
    }

    private static string[] CopyDomains(string[]? domains)
    {
        if (domains is null || domains.Length == 0)
        {
            return Array.Empty<string>();
        }

        string[] copy = new string[domains.Length];

        for (int i = 0; i < domains.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(domains[i]))
            {
                throw new ArgumentException("A domain cannot be null, empty or white space.", nameof(domains));
            }

            copy[i] = domains[i].Trim();
        }

        return copy;
    }

    private static void ValidateFlow(RewriteFlow flow)
    {
        if (flow is not (RewriteFlow.Continue or RewriteFlow.SkipRemainingRules or RewriteFlow.Restart))
        {
            throw new ArgumentOutOfRangeException(nameof(flow), flow, "The flow is not a defined RewriteFlow value.");
        }
    }

    private static void ValidateTarget(RewriteMatchTarget target)
    {
        if (target is not (RewriteMatchTarget.Path or RewriteMatchTarget.PathAndQuery))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "The match target is not a defined RewriteMatchTarget value.");
        }
    }
}

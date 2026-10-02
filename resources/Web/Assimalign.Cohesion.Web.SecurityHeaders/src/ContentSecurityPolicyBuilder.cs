using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.SecurityHeaders.Internal;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// Builds a <see cref="ContentSecurityPolicy"/> (W3C Content Security Policy Level 3) from typed
/// directives instead of a free-form string, so a policy that would not parse the way it reads is
/// rejected when it is built rather than ignored by the user agent.
/// </summary>
/// <remarks>
/// <para>
/// Each method sets one directive. Setting a directive again replaces its value in place, and directives
/// serialize in the order they were first set. Source-list directives take a callback over a
/// <see cref="ContentSecurityPolicySourceListBuilder"/>; the other directives validate their own grammar.
/// <see cref="Directive"/> covers directives this builder does not model (for example
/// <c>require-trusted-types-for</c>), still checked against the generic directive grammar.
/// </para>
/// <para>
/// <c>frame-ancestors</c> is deliberately absent: framing is owned by
/// <see cref="SecurityHeadersPolicy.Framing"/>, which emits the directive together with the matching
/// <c>X-Frame-Options</c> field, so the two can never disagree.
/// </para>
/// </remarks>
public sealed class ContentSecurityPolicyBuilder
{
    private readonly List<ContentSecurityPolicyDirective> _directives = new();

    /// <summary>Sets <c>child-src</c>: the sources of workers and nested browsing contexts.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder ChildSrc(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("child-src", configure);

    /// <summary>Sets <c>connect-src</c>: the URLs scripts may connect to (fetch, XHR, WebSocket, EventSource).</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder ConnectSrc(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("connect-src", configure);

    /// <summary>Sets <c>default-src</c>: the fallback for every fetch directive the policy does not set.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder DefaultSrc(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("default-src", configure);

    /// <summary>Sets <c>font-src</c>: the sources of fonts.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder FontSrc(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("font-src", configure);

    /// <summary>Sets <c>frame-src</c>: the sources this document may load into its frames.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder FrameSrc(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("frame-src", configure);

    /// <summary>Sets <c>img-src</c>: the sources of images and favicons.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder ImgSrc(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("img-src", configure);

    /// <summary>Sets <c>manifest-src</c>: the sources of application manifests.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder ManifestSrc(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("manifest-src", configure);

    /// <summary>Sets <c>media-src</c>: the sources of audio, video and text tracks.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder MediaSrc(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("media-src", configure);

    /// <summary>Sets <c>object-src</c>: the sources of plugins (<c>&lt;object&gt;</c> and <c>&lt;embed&gt;</c>).</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder ObjectSrc(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("object-src", configure);

    /// <summary>Sets <c>script-src</c>: the sources of scripts.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder ScriptSrc(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("script-src", configure);

    /// <summary>Sets <c>script-src-attr</c>: the sources of inline event handlers.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder ScriptSrcAttr(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("script-src-attr", configure);

    /// <summary>Sets <c>script-src-elem</c>: the sources of <c>&lt;script&gt;</c> elements.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder ScriptSrcElem(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("script-src-elem", configure);

    /// <summary>Sets <c>style-src</c>: the sources of stylesheets and inline styles.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder StyleSrc(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("style-src", configure);

    /// <summary>Sets <c>style-src-attr</c>: the sources of <c>style</c> attributes.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder StyleSrcAttr(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("style-src-attr", configure);

    /// <summary>Sets <c>style-src-elem</c>: the sources of <c>&lt;style&gt;</c> and stylesheet <c>&lt;link&gt;</c> elements.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder StyleSrcElem(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("style-src-elem", configure);

    /// <summary>Sets <c>worker-src</c>: the sources of workers.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder WorkerSrc(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("worker-src", configure);

    /// <summary>Sets <c>base-uri</c>: the URLs a <c>&lt;base&gt;</c> element may set.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder BaseUri(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("base-uri", configure);

    /// <summary>Sets <c>form-action</c>: the URLs forms may submit to.</summary>
    /// <param name="configure">The callback that adds the directive's sources.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The source list is empty, or combines <c>'none'</c> with other sources.</exception>
    public ContentSecurityPolicyBuilder FormAction(Action<ContentSecurityPolicySourceListBuilder> configure) => SetSourceList("form-action", configure);

    /// <summary>
    /// Sets <c>sandbox</c>, which applies the HTML sandbox restrictions to the document, lifting only the
    /// named ones. With no tokens, every restriction applies.
    /// </summary>
    /// <param name="allowTokens">The sandbox keywords to allow, such as <c>allow-scripts</c> or <c>allow-forms</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="allowTokens"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A token is not an RFC 9110 token.</exception>
    public ContentSecurityPolicyBuilder Sandbox(params string[] allowTokens)
    {
        ArgumentNullException.ThrowIfNull(allowTokens);

        List<string> tokens = new(allowTokens.Length);
        foreach (string token in allowTokens)
        {
            if (token is null || !SecurityHeadersGrammar.IsToken(token))
            {
                throw new ArgumentException($"'{token}' is not a sandbox keyword (an RFC 9110 token).", nameof(allowTokens));
            }

            string normalized = token.ToLowerInvariant();
            if (!tokens.Contains(normalized))
            {
                tokens.Add(normalized);
            }
        }

        return Set(new ContentSecurityPolicyDirective("sandbox", null, string.Join(' ', tokens)));
    }

    /// <summary>
    /// Sets <c>upgrade-insecure-requests</c>, which makes the user agent fetch this document's insecure
    /// URLs over HTTPS.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public ContentSecurityPolicyBuilder UpgradeInsecureRequests() => Set(new ContentSecurityPolicyDirective("upgrade-insecure-requests", null, string.Empty));

    /// <summary>
    /// Sets <c>report-to</c>, which names the Reporting API endpoint violation reports go to. The
    /// application declares that endpoint in its own <c>Reporting-Endpoints</c> response field.
    /// </summary>
    /// <param name="endpointName">The reporting endpoint name.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpointName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="endpointName"/> is not an RFC 9110 token.</exception>
    public ContentSecurityPolicyBuilder ReportTo(string endpointName)
    {
        ArgumentNullException.ThrowIfNull(endpointName);

        if (!SecurityHeadersGrammar.IsToken(endpointName))
        {
            throw new ArgumentException($"'{endpointName}' is not a reporting endpoint name (an RFC 9110 token).", nameof(endpointName));
        }

        return Set(new ContentSecurityPolicyDirective("report-to", null, endpointName));
    }

    /// <summary>
    /// Sets <c>report-uri</c>, the URLs violation reports are posted to. Deprecated by CSP Level 3 in
    /// favor of <see cref="ReportTo"/>, and still the only reporting directive some user agents honor, so
    /// a policy commonly sets both.
    /// </summary>
    /// <param name="uris">One or more absolute or relative report URLs.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uris"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="uris"/> is empty, or an entry is not a URI reference written without whitespace,
    /// <c>,</c> or <c>;</c>.
    /// </exception>
    public ContentSecurityPolicyBuilder ReportUri(params string[] uris)
    {
        ArgumentNullException.ThrowIfNull(uris);

        if (uris.Length == 0)
        {
            throw new ArgumentException("report-uri needs at least one URL.", nameof(uris));
        }

        foreach (string uri in uris)
        {
            if (uri is null || !SecurityHeadersGrammar.IsReportUri(uri))
            {
                throw new ArgumentException(
                    $"'{uri}' is not a report URL (a URI reference without whitespace, ',' or ';').",
                    nameof(uris));
            }
        }

        return Set(new ContentSecurityPolicyDirective("report-uri", null, string.Join(' ', uris)));
    }

    /// <summary>
    /// Sets a directive this builder does not model, for example
    /// <c>Directive("require-trusted-types-for", "'script'")</c>.
    /// </summary>
    /// <param name="name">The directive name (letters, digits and hyphens; serialized lowercase).</param>
    /// <param name="value">The directive value, or <see langword="null"/> or empty for a valueless directive.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is not a CSP directive name or is <c>frame-ancestors</c>, which
    /// <see cref="SecurityHeadersPolicy.Framing"/> owns; or <paramref name="value"/> holds a character the
    /// CSP directive-value grammar excludes (<c>,</c>, <c>;</c>, a control character, or non-ASCII).
    /// </exception>
    public ContentSecurityPolicyBuilder Directive(string name, string? value = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!SecurityHeadersGrammar.IsDirectiveName(name))
        {
            throw new ArgumentException($"'{name}' is not a CSP directive name (letters, digits and '-').", nameof(name));
        }

        string normalized = name.ToLowerInvariant();
        if (normalized == "frame-ancestors")
        {
            throw new ArgumentException(
                "frame-ancestors is owned by SecurityHeadersPolicy.Framing, which emits it together with a matching X-Frame-Options field.",
                nameof(name));
        }

        string trimmed = value?.Trim(' ') ?? string.Empty;
        if (!SecurityHeadersGrammar.IsDirectiveValue(trimmed))
        {
            throw new ArgumentException(
                $"The value of {normalized} contains a character the CSP directive-value grammar excludes (',', ';', a control character, or non-ASCII).",
                nameof(value));
        }

        return Set(new ContentSecurityPolicyDirective(normalized, null, trimmed));
    }

    /// <summary>
    /// Builds the immutable policy from the directives set so far.
    /// </summary>
    /// <returns>The policy.</returns>
    /// <exception cref="InvalidOperationException">No directive has been set.</exception>
    public ContentSecurityPolicy Build()
    {
        if (_directives.Count == 0)
        {
            throw new InvalidOperationException("A Content Security Policy needs at least one directive.");
        }

        return new ContentSecurityPolicy(_directives.ToArray());
    }

    private ContentSecurityPolicyBuilder SetSourceList(string name, Action<ContentSecurityPolicySourceListBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        ContentSecurityPolicySourceListBuilder sources = new();
        configure(sources);

        return Set(new ContentSecurityPolicyDirective(name, sources.Build(name), null));
    }

    private ContentSecurityPolicyBuilder Set(ContentSecurityPolicyDirective directive)
    {
        for (int index = 0; index < _directives.Count; index++)
        {
            if (string.Equals(_directives[index].Name, directive.Name, StringComparison.Ordinal))
            {
                _directives[index] = directive;
                return this;
            }
        }

        _directives.Add(directive);
        return this;
    }
}

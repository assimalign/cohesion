using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.SecurityHeaders.Internal;

namespace Assimalign.Cohesion.Web.SecurityHeaders;

/// <summary>
/// Builds a <see cref="PermissionsPolicy"/>: the <c>Permissions-Policy</c> field (W3C Permissions Policy),
/// an RFC 9651 Structured Field Dictionary that maps each policy-controlled feature to the origins
/// allowed to use it.
/// </summary>
/// <remarks>
/// <para>
/// Each method sets one feature's allowlist; setting a feature again replaces its allowlist in place,
/// and features serialize in the order they were first set. Feature names are the identifiers the
/// specifications define (<c>camera</c>, <c>geolocation</c>, <c>microphone</c>, <c>payment</c>,
/// <c>fullscreen</c>, …) and must match the RFC 9651 key grammar: lowercase letters, digits, <c>_</c>,
/// <c>-</c>, <c>.</c> and <c>*</c>, starting with a letter. User agents ignore features they do not know.
/// </para>
/// <para>
/// The field is serialized with the Structured Field toolkit of <c>Assimalign.Cohesion.Http</c>, so its
/// syntax is canonical RFC 9651: <c>camera=(), geolocation=(self "https://maps.example.com"), fullscreen=*</c>.
/// </para>
/// </remarks>
public sealed class PermissionsPolicyBuilder
{
    private static readonly StructuredFieldItem _selfItem = new(StructuredFieldBareItem.FromToken("self"));
    private static readonly StructuredFieldMember _allowAll = StructuredFieldMember.FromItem(new StructuredFieldItem(StructuredFieldBareItem.FromToken("*")));
    private static readonly StructuredFieldMember _disabled = StructuredFieldMember.FromInnerList(
        new StructuredFieldInnerList(Array.Empty<StructuredFieldItem>(), StructuredFieldParameters.Empty));

    private readonly List<KeyValuePair<string, StructuredFieldMember>> _features = new();

    /// <summary>
    /// Disables the feature for every origin, the document's own included: <c>feature=()</c>.
    /// </summary>
    /// <param name="feature">The feature name.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="feature"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="feature"/> is not a feature name.</exception>
    public PermissionsPolicyBuilder Disable(string feature) => Set(feature, _disabled);

    /// <summary>
    /// Allows the feature for every origin: <c>feature=*</c>.
    /// </summary>
    /// <param name="feature">The feature name.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="feature"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="feature"/> is not a feature name.</exception>
    public PermissionsPolicyBuilder AllowAll(string feature) => Set(feature, _allowAll);

    /// <summary>
    /// Allows the feature for the document's own origin and, optionally, the listed origins:
    /// <c>feature=(self "https://other.example")</c>.
    /// </summary>
    /// <param name="feature">The feature name.</param>
    /// <param name="origins">Further origins, each <c>scheme://host[:port]</c> (a <c>*.</c> host wildcard is allowed).</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="feature"/> or <paramref name="origins"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="feature"/> is not a feature name, or an origin is not a serialized origin.</exception>
    public PermissionsPolicyBuilder AllowSelf(string feature, params string[] origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        return Set(feature, CreateAllowlist(includeSelf: true, origins));
    }

    /// <summary>
    /// Allows the feature for the listed origins only, not the document's own:
    /// <c>feature=("https://a.example" "https://b.example")</c>.
    /// </summary>
    /// <param name="feature">The feature name.</param>
    /// <param name="origins">One or more origins, each <c>scheme://host[:port]</c> (a <c>*.</c> host wildcard is allowed).</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="feature"/> or <paramref name="origins"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="feature"/> is not a feature name, <paramref name="origins"/> is empty, or an origin
    /// is not a serialized origin.
    /// </exception>
    public PermissionsPolicyBuilder AllowOrigins(string feature, params string[] origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        if (origins.Length == 0)
        {
            throw new ArgumentException("Name at least one origin; use Disable() to allow none.", nameof(origins));
        }

        return Set(feature, CreateAllowlist(includeSelf: false, origins));
    }

    /// <summary>
    /// Builds the immutable policy from the features set so far.
    /// </summary>
    /// <returns>The policy.</returns>
    /// <exception cref="InvalidOperationException">No feature has been set.</exception>
    public PermissionsPolicy Build()
    {
        if (_features.Count == 0)
        {
            throw new InvalidOperationException("A Permissions Policy needs at least one feature.");
        }

        return new PermissionsPolicy(new StructuredFieldDictionary(_features).Serialize());
    }

    private static StructuredFieldMember CreateAllowlist(bool includeSelf, string[] origins)
    {
        List<StructuredFieldItem> items = new(origins.Length + 1);
        if (includeSelf)
        {
            items.Add(_selfItem);
        }

        List<string> seen = new(origins.Length);
        foreach (string origin in origins)
        {
            if (origin is null || !SecurityHeadersGrammar.IsOrigin(origin))
            {
                throw new ArgumentException(
                    $"'{origin}' is not a serialized origin (scheme://host[:port], no path); use AllowAll() for every origin.",
                    nameof(origins));
            }

            if (!seen.Contains(origin))
            {
                seen.Add(origin);
                items.Add(new StructuredFieldItem(StructuredFieldBareItem.FromString(origin)));
            }
        }

        return StructuredFieldMember.FromInnerList(new StructuredFieldInnerList(items, StructuredFieldParameters.Empty));
    }

    private PermissionsPolicyBuilder Set(string feature, StructuredFieldMember allowlist)
    {
        ArgumentNullException.ThrowIfNull(feature);

        if (!SecurityHeadersGrammar.IsFeatureName(feature))
        {
            throw new ArgumentException(
                $"'{feature}' is not a policy-controlled feature name (lowercase letters, digits, '_', '-', '.' and '*', starting with a letter).",
                nameof(feature));
        }

        for (int index = 0; index < _features.Count; index++)
        {
            if (string.Equals(_features[index].Key, feature, StringComparison.Ordinal))
            {
                _features[index] = new KeyValuePair<string, StructuredFieldMember>(feature, allowlist);
                return this;
            }
        }

        _features.Add(new KeyValuePair<string, StructuredFieldMember>(feature, allowlist));
        return this;
    }
}

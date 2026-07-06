using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents a protocol-neutral subject identifier. Covers an OpenID Connect <c>sub</c>
/// (public or pairwise) and a SAML <c>NameID</c> (format URI plus qualifiers) without making
/// either wire format canonical.
/// </summary>
/// <remarks>
/// <para>
/// Equality is defined over <see cref="Value" />, <see cref="Format" />,
/// <see cref="Issuer" />, and <see cref="RelyingPartyQualifier" /> with ordinal comparison —
/// the same identifier scope SAML Core §8.3 mandates for persistent identifiers, which also
/// covers OpenID Connect pairwise subjects scoped to a sector. <see cref="Properties" /> is
/// provenance detail and never participates in equality.
/// </para>
/// <para>
/// Field mapping: <see cref="Issuer" /> carries the asserting scope qualifier — the SAML
/// <c>NameQualifier</c> (which defaults to the assertion issuer when absent) or the OpenID
/// Connect <c>iss</c>. <see cref="RelyingPartyQualifier" /> carries the SAML
/// <c>SPNameQualifier</c> or an OpenID Connect pairwise sector identifier. The SAML
/// <c>SPProvidedID</c> is spec-defined as non-matching and belongs in
/// <see cref="Properties" />. Identifiers used as lookup keys should always carry
/// <see cref="Issuer" />, because subject values are only unique within their issuer.
/// </para>
/// </remarks>
public sealed class SubjectIdentifier : IEquatable<SubjectIdentifier>
{
    private static readonly IReadOnlyDictionary<string, string> emptyProperties =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubjectIdentifier" /> class.
    /// </summary>
    /// <param name="value">The identifier value.</param>
    /// <param name="format">
    /// The identifier format (see <see cref="SubjectIdentifierFormats" />). Null or
    /// whitespace normalizes to <see cref="SubjectIdentifierFormats.Unspecified" /> so that
    /// an absent wire format and an explicit unspecified format compare equal.
    /// </param>
    /// <param name="issuer">The asserting scope qualifier (SAML <c>NameQualifier</c> / OIDC <c>iss</c>).</param>
    /// <param name="relyingPartyQualifier">The relying-party scope qualifier (SAML <c>SPNameQualifier</c> / OIDC pairwise sector).</param>
    /// <param name="properties">Additional provenance detail. The dictionary is copied; keys compare ordinally.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value" /> is null or whitespace.</exception>
    public SubjectIdentifier(
        string value,
        string? format = null,
        string? issuer = null,
        string? relyingPartyQualifier = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Value = value;
        Format = string.IsNullOrWhiteSpace(format) ? SubjectIdentifierFormats.Unspecified : format;
        Issuer = issuer;
        RelyingPartyQualifier = relyingPartyQualifier;
        Properties = properties is null or { Count: 0 }
            ? emptyProperties
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(properties, StringComparer.Ordinal));
    }

    /// <summary>
    /// Gets the identifier value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the identifier format. Never null; an absent format normalizes to
    /// <see cref="SubjectIdentifierFormats.Unspecified" />.
    /// </summary>
    public string Format { get; }

    /// <summary>
    /// Gets the asserting scope qualifier (SAML <c>NameQualifier</c> / OIDC <c>iss</c>).
    /// </summary>
    public string? Issuer { get; }

    /// <summary>
    /// Gets the relying-party scope qualifier (SAML <c>SPNameQualifier</c> / OIDC pairwise
    /// sector). Participates in equality: the same value scoped to two relying parties is
    /// two distinct identities.
    /// </summary>
    public string? RelyingPartyQualifier { get; }

    /// <summary>
    /// Gets additional provenance detail (for example a SAML <c>SPProvidedID</c>). Never
    /// participates in equality.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; }

    /// <inheritdoc />
    public bool Equals(SubjectIdentifier? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return string.Equals(Value, other.Value, StringComparison.Ordinal)
            && string.Equals(Format, other.Format, StringComparison.Ordinal)
            && string.Equals(Issuer, other.Issuer, StringComparison.Ordinal)
            && string.Equals(RelyingPartyQualifier, other.RelyingPartyQualifier, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SubjectIdentifier other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Value, StringComparer.Ordinal);
        hash.Add(Format, StringComparer.Ordinal);
        hash.Add(Issuer, StringComparer.Ordinal);
        hash.Add(RelyingPartyQualifier, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Determines whether two subject identifiers are equal.
    /// </summary>
    /// <param name="left">The first identifier.</param>
    /// <param name="right">The second identifier.</param>
    /// <returns><see langword="true" /> when the identifiers are equal; otherwise <see langword="false" />.</returns>
    public static bool operator ==(SubjectIdentifier? left, SubjectIdentifier? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>
    /// Determines whether two subject identifiers are unequal.
    /// </summary>
    /// <param name="left">The first identifier.</param>
    /// <param name="right">The second identifier.</param>
    /// <returns><see langword="true" /> when the identifiers are unequal; otherwise <see langword="false" />.</returns>
    public static bool operator !=(SubjectIdentifier? left, SubjectIdentifier? right) => !(left == right);

    /// <inheritdoc />
    public override string ToString() => Value;
}

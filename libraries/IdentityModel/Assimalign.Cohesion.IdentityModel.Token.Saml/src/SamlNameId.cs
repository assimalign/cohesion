using System;

namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>NameID</c> (SAML Core §2.2.3) as carried by a materialized SAML
/// token, preserved with wire fidelity — the format, both qualifiers, and the SP-provided
/// identifier. Lift it to the canonical <see cref="SubjectIdentifier" /> through the pinned
/// <see cref="SamlSubjectExtensions.GetSubjectIdentifier(SamlNameId, string?)" /> recipe.
/// </summary>
/// <remarks>
/// This is the token-branch document mirror of the protocol branch's
/// <c>Assimalign.Cohesion.IdentityModel.Protocols.Saml.SamlNameId</c>; the token branch cannot
/// reference the protocol branch (branch independence), so it owns its own copy. A root-tests
/// drift guard pins the NameID recipe equivalent across the two branches. NameID <em>format</em>
/// values use the root <see cref="SubjectIdentifierFormats" /> constants (not a token-branch
/// copy), because the format participates in <see cref="SubjectIdentifier" /> equality.
/// </remarks>
public sealed class SamlNameId
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlNameId" /> class.
    /// </summary>
    /// <param name="value">The identifier value.</param>
    /// <param name="format">The NameID format (see <see cref="SubjectIdentifierFormats" />); null is preserved.</param>
    /// <param name="nameQualifier">The name qualifier (the asserting authority's scope).</param>
    /// <param name="spNameQualifier">The service-provider name qualifier.</param>
    /// <param name="spProvidedId">The service-provider-provided identifier (spec-defined as non-matching).</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value" /> is null or whitespace.</exception>
    public SamlNameId(
        string value,
        string? format = null,
        string? nameQualifier = null,
        string? spNameQualifier = null,
        string? spProvidedId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Value = value;
        Format = format;
        NameQualifier = nameQualifier;
        SpNameQualifier = spNameQualifier;
        SpProvidedId = spProvidedId;
    }

    /// <summary>
    /// Gets the identifier value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the NameID format, as the exact wire value; null when the wire omitted it.
    /// </summary>
    public string? Format { get; }

    /// <summary>
    /// Gets the name qualifier (the asserting authority's scope). When absent, the recipe
    /// defaults it to the assertion issuer.
    /// </summary>
    public string? NameQualifier { get; }

    /// <summary>
    /// Gets the service-provider name qualifier.
    /// </summary>
    public string? SpNameQualifier { get; }

    /// <summary>
    /// Gets the service-provider-provided identifier. SAML defines it as non-matching, so the
    /// recipe carries it in <see cref="SubjectIdentifier.Properties" />.
    /// </summary>
    public string? SpProvidedId { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

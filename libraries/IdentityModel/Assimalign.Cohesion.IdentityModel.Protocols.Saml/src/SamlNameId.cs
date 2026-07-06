using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>NameID</c> (SAML Core §2.2.3), preserved with wire fidelity —
/// the format, both qualifiers, and the SP-provided identifier — so it round-trips
/// losslessly for the token package and the cross-protocol mapper. Lift it to the canonical
/// <see cref="SubjectIdentifier" /> through the pinned
/// <see cref="SamlSubjectExtensions.GetSubjectIdentifier(SamlNameId, string?)" /> recipe.
/// </summary>
public sealed class SamlNameId
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlNameId" /> class.
    /// </summary>
    /// <param name="value">The identifier value.</param>
    /// <param name="format">The NameID format (defaults to the entity format when absent on the wire; null is preserved here).</param>
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
    /// Gets the NameID format, as the exact wire value; null when the wire omitted it (which
    /// SAML treats as the entity format).
    /// </summary>
    public string? Format { get; }

    /// <summary>
    /// Gets the name qualifier (the asserting authority's scope). When absent, callers
    /// default it to the assertion or message issuer.
    /// </summary>
    public string? NameQualifier { get; }

    /// <summary>
    /// Gets the service-provider name qualifier.
    /// </summary>
    public string? SpNameQualifier { get; }

    /// <summary>
    /// Gets the service-provider-provided identifier. SAML defines it as non-matching, so
    /// the canonical recipe carries it in <see cref="SubjectIdentifier.Properties" />.
    /// </summary>
    public string? SpProvidedId { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

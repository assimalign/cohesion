using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Describes the contents of a SAML assertion before it is materialized into an immutable
/// <see cref="SamlAssertion" />.
/// </summary>
public class SamlAssertionDescriptor
{
    /// <summary>
    /// Gets or sets the assertion identifier. Required at materialization — an assertion is
    /// unrecognizable without it.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the SAML version (expected to be <c>2.0</c>). Null is preserved so a
    /// version-less negative fixture is constructible.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the instant the assertion was issued.
    /// </summary>
    public DateTimeOffset? IssueInstant { get; set; }

    /// <summary>
    /// Gets or sets the issuer, modeled as a NameID (SAML Core types <c>Issuer</c> as a
    /// NameID with an optional format and qualifiers).
    /// </summary>
    public SamlNameId? Issuer { get; set; }

    /// <summary>
    /// Gets or sets the subject.
    /// </summary>
    public SamlSubject? Subject { get; set; }

    /// <summary>
    /// Gets or sets the conditions.
    /// </summary>
    public SamlConditions? Conditions { get; set; }

    /// <summary>
    /// Gets the authentication statements.
    /// </summary>
    public IList<SamlAuthnStatement> AuthnStatements { get; } = new List<SamlAuthnStatement>();

    /// <summary>
    /// Gets the attribute statements.
    /// </summary>
    public IList<SamlAttributeStatement> AttributeStatements { get; } = new List<SamlAttributeStatement>();

    /// <summary>
    /// Gets or sets the verbatim, as-received <c>&lt;saml:Assertion&gt;</c> element octets,
    /// preserved for signature re-verification. Must be the exact assertion subtree, never a
    /// re-serialization or a substring of the enclosing response.
    /// </summary>
    public string? RawXml { get; set; }
}

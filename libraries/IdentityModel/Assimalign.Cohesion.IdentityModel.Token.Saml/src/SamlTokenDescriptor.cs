using System;
using System.Collections.Generic;

using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// Describes the contents of a SAML token before it is materialized.
/// </summary>
public sealed class SamlTokenDescriptor : IdentityTokenDescriptor
{
    /// <summary>
    /// Gets or sets the assertion identifier.
    /// </summary>
    public string? AssertionId { get; set; }

    /// <summary>
    /// Gets or sets the SAML assertion version.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the subject name identifier.
    /// </summary>
    public string? NameIdentifier { get; set; }

    /// <summary>
    /// Gets or sets the assertion confirmation method.
    /// </summary>
    public string? ConfirmationMethod { get; set; }

    /// <summary>
    /// Gets or sets the original SAML assertion XML.
    /// </summary>
    public string? AssertionXml
    {
        get => RawData;
        set => RawData = value;
    }

    /// <summary>
    /// Gets the SAML assertion conditions.
    /// </summary>
    public IDictionary<string, object?> Conditions { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}

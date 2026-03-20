using System.Collections.Generic;

using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// Represents a SAML token.
/// </summary>
public interface ISamlToken : IIdentityToken
{
    /// <summary>
    /// Gets the assertion identifier.
    /// </summary>
    string? AssertionId { get; }

    /// <summary>
    /// Gets the SAML assertion version.
    /// </summary>
    string? Version { get; }

    /// <summary>
    /// Gets the SAML name identifier for the subject.
    /// </summary>
    string? NameIdentifier { get; }

    /// <summary>
    /// Gets the assertion confirmation method.
    /// </summary>
    string? ConfirmationMethod { get; }

    /// <summary>
    /// Gets the original SAML assertion XML.
    /// </summary>
    string? AssertionXml { get; }

    /// <summary>
    /// Gets the SAML assertion conditions.
    /// </summary>
    IReadOnlyDictionary<string, object?> Conditions { get; }

    /// <summary>
    /// Attempts to read a SAML condition value.
    /// </summary>
    /// <param name="conditionName">The condition name.</param>
    /// <param name="value">When this method returns, contains the condition value, if one exists.</param>
    /// <returns><see langword="true" /> when the condition exists; otherwise <see langword="false" />.</returns>
    bool TryGetCondition(string conditionName, out object? value);
}

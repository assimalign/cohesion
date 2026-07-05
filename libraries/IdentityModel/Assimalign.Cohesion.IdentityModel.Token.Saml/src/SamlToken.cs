using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// Represents an immutable SAML token.
/// </summary>
public sealed class SamlToken : IdentityToken, ISamlToken
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlToken" /> class.
    /// </summary>
    /// <param name="descriptor">The SAML descriptor.</param>
    public SamlToken(SamlTokenDescriptor descriptor)
        : base(IdentityTokenKind.Saml, descriptor)
    {
        AssertionId = descriptor.AssertionId;
        Version = descriptor.Version;
        NameIdentifier = descriptor.NameIdentifier;
        ConfirmationMethod = descriptor.ConfirmationMethod;
        Conditions = new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(descriptor.Conditions, StringComparer.Ordinal));
    }

    /// <inheritdoc />
    public string? AssertionId { get; }

    /// <inheritdoc />
    public string? Version { get; }

    /// <inheritdoc />
    public string? NameIdentifier { get; }

    /// <inheritdoc />
    public string? ConfirmationMethod { get; }

    /// <inheritdoc />
    public string? AssertionXml => RawData;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Conditions { get; }

    /// <inheritdoc />
    public bool TryGetCondition(string conditionName, out object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conditionName);
        return Conditions.TryGetValue(conditionName, out value);
    }
}

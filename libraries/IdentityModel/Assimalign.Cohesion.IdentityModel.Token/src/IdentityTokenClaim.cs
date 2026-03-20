using System;
using System.Collections;

namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Represents an immutable claim carried by an identity token.
/// </summary>
public sealed class IdentityTokenClaim : IIdentityTokenClaim
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityTokenClaim" /> class.
    /// </summary>
    /// <param name="type">The claim type.</param>
    /// <param name="value">The claim value.</param>
    public IdentityTokenClaim(string type, object? value)
        : this(type, value, InferValueKind(value))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityTokenClaim" /> class.
    /// </summary>
    /// <param name="type">The claim type.</param>
    /// <param name="value">The claim value.</param>
    /// <param name="valueKind">The normalized claim value shape.</param>
    /// <param name="valueType">The optional wire-level value type.</param>
    /// <param name="issuer">The claim issuer.</param>
    /// <param name="originalIssuer">The original claim issuer.</param>
    public IdentityTokenClaim(
        string type,
        object? value,
        IdentityTokenValueKind valueKind,
        string? valueType = null,
        string? issuer = null,
        string? originalIssuer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        Type = type;
        Value = value;
        ValueKind = valueKind;
        ValueType = valueType;
        Issuer = issuer;
        OriginalIssuer = originalIssuer ?? issuer;
    }

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public object? Value { get; }

    /// <inheritdoc />
    public IdentityTokenValueKind ValueKind { get; }

    /// <inheritdoc />
    public string? ValueType { get; }

    /// <inheritdoc />
    public string? Issuer { get; }

    /// <inheritdoc />
    public string? OriginalIssuer { get; }

    private static IdentityTokenValueKind InferValueKind(object? value)
    {
        if (value is null)
        {
            return IdentityTokenValueKind.Null;
        }

        return value switch
        {
            string => IdentityTokenValueKind.String,
            bool => IdentityTokenValueKind.Boolean,
            byte[] => IdentityTokenValueKind.Binary,
            DateTime or DateTimeOffset => IdentityTokenValueKind.DateTime,
            sbyte or byte or short or ushort or int or uint or long or ulong => IdentityTokenValueKind.Integer,
            Half or float or double or decimal => IdentityTokenValueKind.Decimal,
            IDictionary => IdentityTokenValueKind.Object,
            IEnumerable => IdentityTokenValueKind.Array,
            _ => IdentityTokenValueKind.Object
        };
    }
}

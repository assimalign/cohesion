using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Represents an immutable JOSE header (RFC 7515 §4). The typed accessors are computed
/// projections of the single authoritative <see cref="Parameters" /> record, so the header
/// can never present a typed value that disagrees with its raw parameters.
/// </summary>
public sealed class JoseHeader
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JoseHeader" /> class by snapshotting the
    /// provided descriptor.
    /// </summary>
    /// <param name="descriptor">The header contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a parameter name is blank or a parameter value is undefined.
    /// </exception>
    public JoseHeader(JoseHeaderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Parameters = SnapshotParameters(descriptor.Parameters);
    }

    /// <summary>
    /// Gets the raw header parameters, keyed by JOSE parameter name.
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Parameters { get; }

    /// <summary>
    /// Gets the signing algorithm (<c>alg</c>), as the exact wire string.
    /// </summary>
    public string? Algorithm => GetString(JoseHeaderParameterNames.Algorithm);

    /// <summary>
    /// Gets the token type (<c>typ</c>).
    /// </summary>
    public string? Type => GetString(JoseHeaderParameterNames.Type);

    /// <summary>
    /// Gets the content type (<c>cty</c>).
    /// </summary>
    public string? ContentType => GetString(JoseHeaderParameterNames.ContentType);

    /// <summary>
    /// Gets the key identifier (<c>kid</c>).
    /// </summary>
    public string? KeyId => GetString(JoseHeaderParameterNames.KeyId);

    /// <summary>
    /// Gets a value indicating whether the header declares the unsecured <c>none</c>
    /// algorithm.
    /// </summary>
    public bool IsUnsecured => string.Equals(Algorithm, JoseAlgorithms.None, StringComparison.Ordinal);

    /// <summary>
    /// Gets the critical header parameter names (<c>crit</c>, RFC 7515 §4.1.11) the recipient
    /// must understand. Empty when the header declares none.
    /// </summary>
    public IReadOnlyList<string> Critical
    {
        get
        {
            if (!Parameters.TryGetValue(JoseHeaderParameterNames.Critical, out var value) ||
                value.Kind != IdentityValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>();
            foreach (var element in value.AsArray())
            {
                if (element.TryGetString(out var name))
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the payload is base64url-encoded (RFC 7797 <c>b64</c>).
    /// Defaults to <see langword="true" />; an explicit <c>b64:false</c> header selects the
    /// unencoded-payload variant, which this package does not support.
    /// </summary>
    public bool RequiresBase64Payload
    {
        get
        {
            if (Parameters.TryGetValue(JoseHeaderParameterNames.Base64Payload, out var value) &&
                value.TryGetBoolean(out var encoded))
            {
                return encoded;
            }

            return true;
        }
    }

    private string? GetString(string name)
        => Parameters.TryGetValue(name, out var value) && value.TryGetString(out var text) ? text : null;

    private static IReadOnlyDictionary<string, IdentityClaimValue> SnapshotParameters(
        IDictionary<string, IdentityClaimValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.Count == 0)
        {
            return ReadOnlyDictionary<string, IdentityClaimValue>.Empty;
        }

        var copy = new Dictionary<string, IdentityClaimValue>(parameters.Count, StringComparer.Ordinal);
        foreach (var (name, value) in parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(parameters));

            if (value.IsUndefined)
            {
                throw new ArgumentException(
                    $"The JOSE header parameter '{name}' must not have an undefined value.",
                    nameof(parameters));
            }

            copy[name] = value;
        }

        return new ReadOnlyDictionary<string, IdentityClaimValue>(copy);
    }
}

using System;
using System.Collections.Generic;

using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Describes the contents of a JSON Web Token before it is materialized.
/// </summary>
public sealed class JsonWebTokenDescriptor : IdentityTokenDescriptor
{
    /// <summary>
    /// Gets or sets the declared signing algorithm.
    /// </summary>
    public string? Algorithm { get; set; }

    /// <summary>
    /// Gets or sets the compact token parts.
    /// </summary>
    public JsonWebTokenParts? Parts { get; set; }

    /// <summary>
    /// Gets the JWT header values.
    /// </summary>
    public IDictionary<string, object?> Header { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}

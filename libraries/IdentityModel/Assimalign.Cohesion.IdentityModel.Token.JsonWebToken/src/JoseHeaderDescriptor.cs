using System;
using System.Collections.Generic;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Describes the contents of a JOSE header before it is materialized into an immutable
/// <see cref="JoseHeader" />. Parameters are the raw, typed header record; the materialized
/// header's typed accessors are computed projections of it, so the two cannot disagree.
/// </summary>
public class JoseHeaderDescriptor
{
    /// <summary>
    /// Gets the header parameters, keyed by JOSE parameter name (see
    /// <see cref="JoseHeaderParameterNames" />). Set well-known parameters through those name
    /// constants; the value model is the canonical <see cref="IdentityClaimValue" />.
    /// </summary>
    public IDictionary<string, IdentityClaimValue> Parameters { get; } =
        new Dictionary<string, IdentityClaimValue>(StringComparer.Ordinal);
}

using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Describes the contents of a claim mapper before it is materialized into an immutable
/// <see cref="IdentityClaimMapper" />. Custom mappings are deployment-lifetime configuration —
/// real identity providers emit custom attribute names — layered over the default table.
/// </summary>
public class IdentityClaimMapperDescriptor
{
    /// <summary>
    /// Gets the caller-supplied wire-name to canonical-type mappings, keyed ordinally. On a key
    /// collision a custom mapping wins over the default table; an identity entry (a name mapped
    /// to itself) suppresses a single default mapping.
    /// </summary>
    public IDictionary<string, string> CustomMappings { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets whether the <see cref="IdentityClaimMappings.Default" /> table is included.
    /// Defaults to <see langword="true" />; set <see langword="false" /> to canonicalize with
    /// custom mappings only.
    /// </summary>
    public bool IncludeDefaultMappings { get; set; } = true;
}

using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Cors;

/// <summary>
/// Describes the default and named policies of an opt-in dynamically configured CORS pipeline.
/// </summary>
/// <remarks>
/// A <see cref="CorsPolicyStore"/> copies and validates this configuration before publishing it.
/// Changing this object afterwards does not change the running pipeline.
/// </remarks>
public sealed class CorsConfiguration
{
    /// <summary>
    /// Gets or sets the policy for requests without CORS endpoint metadata; <see langword="null"/>
    /// means no default policy.
    /// </summary>
    public CorsPolicyConfiguration? DefaultPolicy { get; set; }

    /// <summary>
    /// Gets or sets the named policies referenced by endpoints. Names compare ordinally and are
    /// case-sensitive. The default collection is empty.
    /// </summary>
    public Dictionary<string, CorsPolicyConfiguration> Policies { get; set; } = new(StringComparer.Ordinal);
}

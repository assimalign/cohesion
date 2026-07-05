using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 metadata <c>Organization</c> (SAML Metadata §2.3.2.1).
/// </summary>
public sealed class SamlOrganization
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlOrganization" /> class.
    /// </summary>
    /// <param name="name">The organization name.</param>
    /// <param name="displayName">The organization display name.</param>
    /// <param name="url">The organization URL.</param>
    public SamlOrganization(string? name = null, string? displayName = null, string? url = null)
    {
        Name = name;
        DisplayName = displayName;
        Url = url;
    }

    /// <summary>
    /// Gets the organization name.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Gets the organization display name.
    /// </summary>
    public string? DisplayName { get; }

    /// <summary>
    /// Gets the organization URL.
    /// </summary>
    public string? Url { get; }
}

using System;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// Declares a resource type an assembly's provider serves. A catalog generator reads these
/// attributes to build the provider's <see cref="ResourceProviderManifest"/>, so the catalog is
/// closed per build and needs no reflection at run time.
/// </summary>
/// <remarks>
/// The catalog generator is not written yet; until it is, a provider builds its manifest by hand
/// from the same values.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ResourceTypeAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceTypeAttribute"/> class.
    /// </summary>
    /// <param name="type">The resource type, such as <c>Cohesion.IdentityHub/identityHubs</c>.</param>
    public ResourceTypeAttribute(string type)
    {
        Type = type;
    }

    /// <summary>
    /// Gets the resource type.
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Gets or sets the API versions the provider accepts.
    /// </summary>
    public string[] ApiVersions { get; set; } = [];

    /// <summary>
    /// Gets or sets, for a composite, the roles of its private children.
    /// </summary>
    public string[] Tiers { get; set; } = [];
}

using System;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// Declares an action on a resource type an assembly's provider serves.
/// </summary>
/// <remarks>
/// Built-in roles over these actions are part of the permission model, which is not settled
/// (the v2 design's decision 4), so no role attribute ships yet.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ResourceActionAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceActionAttribute"/> class.
    /// </summary>
    /// <param name="type">The resource type the action belongs to.</param>
    /// <param name="name">The action name, such as <c>rotate-signing-key</c>.</param>
    public ResourceActionAttribute(string type, string name)
    {
        Type = type;
        Name = name;
    }

    /// <summary>
    /// Gets the resource type the action belongs to.
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Gets the action name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the action is sensitive: a role grants it only by name.
    /// </summary>
    public bool Sensitive { get; set; }
}

using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>A logical container of resource names within one application.</summary>
/// <remarks>A group changes presentation only. It creates no deployment, identity, authorization, or lifecycle boundary.</remarks>
public sealed record ApplicationResourceGroup
{
    /// <summary>Creates an immutable logical group snapshot.</summary>
    /// <param name="name">The application-local group name.</param>
    /// <param name="resources">The member resource names in declaration order.</param>
    /// <exception cref="ArgumentException">A name is invalid or a member is duplicated.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="resources"/> is null.</exception>
    public ApplicationResourceGroup(ResourceGroupName name, IReadOnlyList<ResourceName> resources)
    {
        if (!ResourceGroupName.IsValid(name.Value, out _))
        {
            throw new ArgumentException(ErrorMessages.ResourceGroupNameInvalid, nameof(name));
        }
        ArgumentNullException.ThrowIfNull(resources);
        var members = new ResourceName[resources.Count];
        var seen = new HashSet<ResourceName>();
        for (int index = 0; index < members.Length; index++)
        {
            ResourceName resource = resources[index];
            if (string.IsNullOrWhiteSpace(resource.Value) || !seen.Add(resource))
            {
                throw new ArgumentException(ErrorMessages.ResourceGroupMembersInvalid, nameof(resources));
            }
            members[index] = resource;
        }
        Name = name;
        Resources = Array.AsReadOnly(members);
    }

    /// <summary>Gets the logical group name.</summary>
    public ResourceGroupName Name { get; }

    /// <summary>Gets member resource names in declaration order.</summary>
    public IReadOnlyList<ResourceName> Resources { get; }
}

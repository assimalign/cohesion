using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Describes a logical group's members in a portable application model.</summary>
public sealed record ApplicationModelResourceGroupDocument
{
    /// <summary>Creates a portable group declaration.</summary>
    /// <param name="name">The application-local group name.</param>
    /// <param name="resources">The member resource names.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    [JsonConstructor]
    public ApplicationModelResourceGroupDocument(string name, IReadOnlyList<string> resources)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(resources);
        var members = new string[resources.Count];
        for (int index = 0; index < members.Length; index++)
        {
            members[index] = resources[index];
        }
        Name = name;
        Resources = Array.AsReadOnly(members);
    }

    /// <summary>Gets the logical group name.</summary>
    public string Name { get; }

    /// <summary>Gets the member resource names.</summary>
    public IReadOnlyList<string> Resources { get; }
}

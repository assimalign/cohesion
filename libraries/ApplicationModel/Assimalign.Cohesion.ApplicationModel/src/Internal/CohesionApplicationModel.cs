using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The default <see cref="IApplicationModel"/>. <see cref="Descriptors"/> is authoritative;
/// <see cref="Resources"/> is a one-to-one projection of it.
/// </summary>
internal sealed class CohesionApplicationModel : IApplicationModel
{
    public CohesionApplicationModel(
        ApplicationName name,
        IApplicationEnvironment environment,
        IReadOnlyList<IApplicationResourceDescriptor> descriptors)
    {
        Name = name;
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        Descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));

        var resources = new IApplicationResource[descriptors.Count];
        for (int i = 0; i < descriptors.Count; i++)
        {
            resources[i] = descriptors[i].Resource;
        }

        Resources = resources;
    }

    public ApplicationName Name { get; }

    public IApplicationEnvironment Environment { get; }

    public IReadOnlyList<IApplicationResourceDescriptor> Descriptors { get; }

    public IReadOnlyList<IApplicationResource> Resources { get; }
}

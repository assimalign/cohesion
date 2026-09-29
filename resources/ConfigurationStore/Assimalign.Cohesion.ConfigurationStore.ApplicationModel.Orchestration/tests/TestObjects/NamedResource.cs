using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// A resource with a name and no manifest, whose kind only <see cref="IApplicationBuilder.Build"/>
/// can check.
/// </summary>
internal sealed class NamedResource : IApplicationResource
{
    internal NamedResource(string name)
    {
        Name = (ResourceName)name;
    }

    public ResourceName Name { get; }
}

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>A typed ConfigurationStore graph descriptor for declaring dependencies and resource commands.</summary>
public interface IConfigurationStoreResourceDescriptor : IResourceCommandDescriptor
{
    /// <summary>Declares a dependency while retaining the area's typed command surface.</summary>
    /// <param name="resource">The resource that must run first.</param>
    /// <returns>This typed descriptor.</returns>
    /// <exception cref="System.ArgumentNullException">The dependency is null.</exception>
    /// <exception cref="System.InvalidOperationException">A self dependency is declared or the model is built.</exception>
    new IConfigurationStoreResourceDescriptor DependsOn(IApplicationResourceDescriptor resource);

    /// <summary>Declares dependencies while retaining the area's typed command surface.</summary>
    /// <param name="resources">The resources that must run first.</param>
    /// <returns>This typed descriptor.</returns>
    /// <exception cref="System.ArgumentNullException">The dependencies or an entry are null.</exception>
    /// <exception cref="System.InvalidOperationException">A self dependency is declared or the model is built.</exception>
    new IConfigurationStoreResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources);
}
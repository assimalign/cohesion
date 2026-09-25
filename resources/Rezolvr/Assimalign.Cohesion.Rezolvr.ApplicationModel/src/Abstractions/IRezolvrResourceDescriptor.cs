using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Describes a Rezolvr resource, its dependencies, and its declarative commands.</summary>
public interface IRezolvrResourceDescriptor : IResourceCommandDescriptor
{
    /// <summary>Gets the typed resource wrapped by this descriptor.</summary>
    new RezolvrResource Resource { get; }

    /// <summary>Declares a dependency while retaining the typed command surface.</summary>
    /// <param name="resource">The resource that must run first.</param>
    /// <returns>This typed descriptor.</returns>
    /// <exception cref="System.ArgumentNullException">The dependency is null.</exception>
    /// <exception cref="System.InvalidOperationException">A self dependency is declared or the model is built.</exception>
    new IRezolvrResourceDescriptor DependsOn(IApplicationResourceDescriptor resource);

    /// <summary>Declares dependencies while retaining the typed command surface.</summary>
    /// <param name="resources">The resources that must run first.</param>
    /// <returns>This typed descriptor.</returns>
    /// <exception cref="System.ArgumentNullException">The dependencies or an entry are null.</exception>
    /// <exception cref="System.InvalidOperationException">A self dependency is declared or the model is built.</exception>
    new IRezolvrResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources);
}

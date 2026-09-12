using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.LogSpace.ApplicationModel;

/// <summary>
/// Describes a LogSpace resource together with its application-graph dependencies.
/// </summary>
public interface ILogSpaceResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed LogSpace resource wrapped by this descriptor.
    /// </summary>
    new LogSpaceResource Resource { get; }
}

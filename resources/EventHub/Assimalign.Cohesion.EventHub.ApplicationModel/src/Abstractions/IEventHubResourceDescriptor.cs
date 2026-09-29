using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes a EventHub resource together with its application-graph dependencies.
/// </summary>
public interface IEventHubResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed EventHub resource wrapped by this descriptor.
    /// </summary>
    new EventHubResource Resource { get; }
}

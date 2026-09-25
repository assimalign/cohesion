using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes a IoTHub resource together with its application-graph dependencies.
/// </summary>
public interface IIoTHubResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed IoTHub resource wrapped by this descriptor.
    /// </summary>
    new IoTHubResource Resource { get; }
}

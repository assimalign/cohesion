using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.MediaHub.ApplicationModel;

/// <summary>
/// Describes a MediaHub resource together with its application-graph dependencies.
/// </summary>
public interface IMediaHubResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed MediaHub resource wrapped by this descriptor.
    /// </summary>
    new MediaHubResource Resource { get; }
}

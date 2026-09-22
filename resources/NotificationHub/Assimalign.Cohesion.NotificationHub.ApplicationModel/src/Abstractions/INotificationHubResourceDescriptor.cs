using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes a NotificationHub resource together with its application-graph dependencies.
/// </summary>
public interface INotificationHubResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed NotificationHub resource wrapped by this descriptor.
    /// </summary>
    new NotificationHubResource Resource { get; }
}

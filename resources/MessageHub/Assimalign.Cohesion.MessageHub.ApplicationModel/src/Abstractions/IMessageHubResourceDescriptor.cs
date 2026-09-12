using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.MessageHub.ApplicationModel;

/// <summary>
/// Describes a MessageHub resource together with its application-graph dependencies.
/// </summary>
public interface IMessageHubResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed MessageHub resource wrapped by this descriptor.
    /// </summary>
    new MessageHubResource Resource { get; }
}

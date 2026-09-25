using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes a EmailHub resource together with its application-graph dependencies.
/// </summary>
public interface IEmailHubResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed EmailHub resource wrapped by this descriptor.
    /// </summary>
    new EmailHubResource Resource { get; }
}

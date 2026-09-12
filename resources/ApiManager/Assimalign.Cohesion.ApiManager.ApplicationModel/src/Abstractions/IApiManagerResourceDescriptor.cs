using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApiManager.ApplicationModel;

/// <summary>
/// Describes a ApiManager resource together with its application-graph dependencies.
/// </summary>
public interface IApiManagerResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed ApiManager resource wrapped by this descriptor.
    /// </summary>
    new ApiManagerResource Resource { get; }
}

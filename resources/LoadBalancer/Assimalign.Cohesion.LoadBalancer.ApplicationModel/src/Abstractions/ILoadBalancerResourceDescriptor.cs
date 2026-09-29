using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Describes a LoadBalancer resource together with its application-graph dependencies.
/// </summary>
public interface ILoadBalancerResourceDescriptor : IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the typed LoadBalancer resource wrapped by this descriptor.
    /// </summary>
    new LoadBalancerResource Resource { get; }
}

using System.IO;

namespace Assimalign.Cohesion.LoadBalancer;

/// <summary>
/// Describes the LoadBalancer application context without hosting dependencies.
/// </summary>
public interface ILoadBalancerApplicationContext
{
    /// <summary>
    /// Gets the base directory in which the application runs, when configured.
    /// </summary>
    FileSystemPath? ContentRootPath { get; }
}

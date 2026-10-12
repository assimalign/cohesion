using System;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Owns disposable resources created while composing an application's middleware pipeline.
/// </summary>
/// <remarks>
/// An application context may implement this optional composition seam. Registered resources
/// remain alive across application stops and restarts and are disposed during final application
/// teardown, after request processing has drained. Registration does not make a resource a service
/// or a request feature.
/// </remarks>
public interface IWebApplicationLifetime
{
    /// <summary>
    /// Transfers ownership of a resource to the application for disposal during final teardown.
    /// </summary>
    /// <param name="resource">The resource whose ownership transfers when this method returns.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">Application teardown has already begun.</exception>
    /// <remarks>
    /// Registering the same object more than once has no additional effect. Resources are disposed
    /// in reverse registration order. A disposal failure does not prevent the remaining resources
    /// from being disposed. If registration throws, the caller retains ownership of the resource.
    /// </remarks>
    void RegisterForDisposal(IDisposable resource);
}

using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

internal sealed class EnabledResourcePipeline : IWebApplicationPipeline
{
    private readonly IResourceControlPlane _controlPlane;
    private readonly ResourceContext _resourceContext;
    private readonly WebApplicationContext _applicationContext;
    private readonly int? _controlPlanePort;
    private readonly IWebApplicationPipeline _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnabledResourcePipeline"/> class.
    /// </summary>
    /// <param name="controlPlane">The resource control plane that serves control-plane requests.</param>
    /// <param name="resourceContext">The ambient resource context the control plane runs under.</param>
    /// <param name="applicationContext">The Web application context whose host state gates control-plane requests.</param>
    /// <param name="controlPlanePort">The listener port that carries control-plane requests, or <see langword="null"/> when the ambient listener is unknown and the control plane is disabled.</param>
    /// <param name="next">The application pipeline that receives every request the control plane does not handle.</param>
    public EnabledResourcePipeline(
        IResourceControlPlane controlPlane,
        ResourceContext resourceContext,
        WebApplicationContext applicationContext,
        int? controlPlanePort,
        IWebApplicationPipeline next)
    {
        _controlPlane = controlPlane;
        _resourceContext = resourceContext;
        _applicationContext = applicationContext;
        _controlPlanePort = controlPlanePort;
        _next = next;
    }

    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default) =>
        // An unknown ambient listener disables Web's control plane. The shared terminal's null
        // port means no gate, so this wrapper must forward without invoking it in that case.
        _controlPlanePort is null
            ? _next.ExecuteAsync(context, cancellationToken)
            : ResourceControlPlaneMiddleware.InvokeAsync(_controlPlane, _resourceContext,
                _applicationContext.State is HostState.Started, _controlPlanePort, context,
                nextContext => _next.ExecuteAsync(nextContext, cancellationToken));
}

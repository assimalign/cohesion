using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

internal sealed class EnabledResourcePipeline(
    IResourceControlPlane controlPlane,
    ResourceContext resourceContext,
    WebApplicationContext applicationContext,
    int? controlPlanePort,
    IWebApplicationPipeline next) : IWebApplicationPipeline
{
    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default) =>
        // An unknown ambient listener disables Web's control plane. The shared terminal's null
        // port means no gate, so this wrapper must forward without invoking it in that case.
        controlPlanePort is null
            ? next.ExecuteAsync(context, cancellationToken)
            : ResourceControlPlaneMiddleware.InvokeAsync(controlPlane, resourceContext,
                applicationContext.State is HostState.Started, controlPlanePort, context,
                nextContext => next.ExecuteAsync(nextContext, cancellationToken));
}

using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

internal sealed class ControlPlaneRouteHandler : IRouterRouteHandler
{
    private readonly Func<IHttpContext, CancellationToken, Task> _handler;

    public ControlPlaneRouteHandler(Func<IHttpContext, CancellationToken, Task> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public Task InvokeAsync(
        IHttpContext context,
        CancellationToken cancellationToken = default) =>
        _handler(context, cancellationToken);
}

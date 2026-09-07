using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// Places the registered resource control plane ahead of the application's final pipeline,
/// including a pipeline supplied through <see cref="IWebApplicationBuilder.AddPipeline"/>.
/// </summary>
internal sealed class ResourceControlPlanePipeline : IWebApplicationPipeline
{
    private readonly IResourceControlPlane _controlPlane;
    private readonly WebApplicationContext _applicationContext;
    private readonly IWebApplicationPipeline _next;
    private readonly int? _controlPlanePort;

    internal ResourceControlPlanePipeline(
        IResourceControlPlane controlPlane,
        ReadOnlyMemory<byte> bootstrapCredential,
        bool requireAuthentication,
        WebApplicationContext applicationContext,
        IWebApplicationPipeline next)
    {
        _controlPlane = controlPlane ?? throw new ArgumentNullException(nameof(controlPlane));
        BootstrapCredential = bootstrapCredential.ToArray();
        RequireAuthentication = requireAuthentication;
        _applicationContext = applicationContext ?? throw new ArgumentNullException(nameof(applicationContext));
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _controlPlanePort = controlPlane.ObservedEndpoints.TryGetValue(
            "http",
            out Uri? endpoint)
            ? endpoint.Port
            : null;
    }

    private ReadOnlyMemory<byte> BootstrapCredential { get; }

    private bool RequireAuthentication { get; }

    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ResourceControlPlaneMiddleware.InvokeAsync(
            _controlPlane,
            BootstrapCredential,
            RequireAuthentication,
            _controlPlanePort,
            _applicationContext.State is HostState.Started,
            context,
            nextContext => _next.ExecuteAsync(nextContext, cancellationToken));
    }
}

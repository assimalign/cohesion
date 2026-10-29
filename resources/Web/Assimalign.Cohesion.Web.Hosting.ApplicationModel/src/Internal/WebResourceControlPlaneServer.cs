using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Internal;

/// <summary>
/// Serves a resource instance's control plane on its own listener, separate from the application's
/// listeners and pipeline: Web.Server's engine with a single handler, and no host of its own.
/// </summary>
/// <remarks>
/// The server is a host service of its own, next to the resource service. A host starts it after
/// every lifecycle service's <see cref="IHostLifecycleService.StartingAsync"/>, so it listens only
/// once the resource service has published the manifest and restored the gateway's state. The Web
/// host stops the application's servers before its other services, so the control plane stays
/// reachable while the application drains. Each start creates a new engine, because a stopped engine
/// does not start again.
/// </remarks>
internal sealed class WebResourceControlPlaneServer : IHostService
{
    private readonly IPEndPoint _endPoint;
    private readonly IApplicationInstanceControlPlane _controlPlane;
    private WebApplicationServer? _server;

    public WebResourceControlPlaneServer(IPEndPoint endPoint, IApplicationInstanceControlPlane controlPlane)
    {
        _endPoint = endPoint;
        _controlPlane = controlPlane;
    }

    public ServiceId Id { get; } = ServiceId.New();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var options = new WebApplicationServerOptions
        {
            Pipeline = new ControlPlanePipeline(_controlPlane),
        };
        options.Listener.UseHttp1(tcp => tcp.EndPoint = _endPoint);

        var server = new WebApplicationServer(options);
        try
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The engine releases its listener only when it stops.
            await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _server = server;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _server, null) is { } server)
        {
            await server.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the listener a control-plane endpoint names.
    /// </summary>
    /// <exception cref="ArgumentException">The endpoint is not an absolute http URL whose host is an IP address or localhost.</exception>
    public static IPEndPoint ParseEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException(ErrorMessages.ControlPlaneEndpointInvalid, nameof(endpoint));
        }

        IPAddress? address = string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.TryParse(endpoint.Host, out IPAddress? parsed) ? parsed : null;

        return address is null
            ? throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ControlPlaneEndpointInvalid), nameof(endpoint))
            : new IPEndPoint(address, endpoint.Port);
    }

    // The engine's whole pipeline: every request goes to the control plane's routes.
    private sealed class ControlPlanePipeline : IWebApplicationPipeline
    {
        private readonly IApplicationInstanceControlPlane _controlPlane;

        public ControlPlanePipeline(IApplicationInstanceControlPlane controlPlane)
        {
            _controlPlane = controlPlane;
        }

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
        {
            return ControlPlaneHttpHandler.InvokeAsync(_controlPlane, context);
        }
    }
}

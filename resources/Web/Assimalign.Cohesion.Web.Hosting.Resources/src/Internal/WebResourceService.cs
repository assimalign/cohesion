using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Internal;

/// <summary>
/// Runs a Web resource instance: a private Web host that serves the instance's control plane on its
/// own listener, separate from the application's listeners and pipeline, and the control plane.
/// </summary>
/// <remarks>
/// Starting opens the listener first, so the control plane is reachable as soon as the gateway has
/// its manifest, then starts the control plane, which publishes and restores; until it has started,
/// invocations answer 503. A control plane that fails to start closes the listener again. Disposal
/// releases the private host.
/// </remarks>
internal sealed class WebResourceService : IResourceService, IAsyncDisposable
{
    private readonly IPEndPoint _endPoint;
    private WebApplication? _application;

    public WebResourceService(IResourceControlPlane controlPlane, IPEndPoint endPoint)
    {
        ControlPlane = controlPlane;
        _endPoint = endPoint;
    }

    public ServiceId Id { get; } = ServiceId.New();

    public IResourceControlPlane ControlPlane { get; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions());
        builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = _endPoint));

        WebApplication application = builder.Build();
        IResourceControlPlane controlPlane = ControlPlane;
        application.Use((context, _) => ControlPlaneHttpHandler.InvokeAsync(controlPlane, context));
        _application = application;

        await ((IWebApplication)application).StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await controlPlane.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ((IWebApplication)application).StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await ControlPlane.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_application is { } application)
        {
            await ((IWebApplication)application).StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is { } application)
        {
            await ((IAsyncDisposable)application).DisposeAsync().ConfigureAwait(false);
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
}

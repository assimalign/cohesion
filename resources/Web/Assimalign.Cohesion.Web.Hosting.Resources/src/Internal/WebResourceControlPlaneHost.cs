using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Internal;

/// <summary>
/// The private Web host that serves a resource instance's control plane on its own listener,
/// separate from the application's listeners and pipeline.
/// </summary>
/// <remarks>
/// It is registered ahead of the control plane, so it listens before the control plane publishes
/// the manifest; until the control plane has started, invocations answer 503. Disposal releases
/// the host and the manifest publisher.
/// </remarks>
internal sealed class WebResourceControlPlaneHost : IHostService, IAsyncDisposable
{
    private readonly IResourceControlPlane _controlPlane;
    private readonly IPEndPoint _endPoint;
    private readonly IDisposable _publisher;
    private WebApplication? _application;

    public WebResourceControlPlaneHost(IResourceControlPlane controlPlane, IPEndPoint endPoint, IDisposable publisher)
    {
        _controlPlane = controlPlane;
        _endPoint = endPoint;
        _publisher = publisher;
    }

    public ServiceId Id { get; } = ServiceId.New();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions());
        builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = _endPoint));

        WebApplication application = builder.Build();
        application.Use((context, _) => ControlPlaneHttpHandler.InvokeAsync(_controlPlane, context));
        _application = application;

        await ((IWebApplication)application).StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _application is { } application
            ? ((IWebApplication)application).StopAsync(cancellationToken)
            : Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is { } application)
        {
            await ((IAsyncDisposable)application).DisposeAsync().ConfigureAwait(false);
        }

        _publisher.Dispose();
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

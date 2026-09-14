using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.ControlPlane;
using Assimalign.Cohesion.Web.Hosting;

namespace Assimalign.Cohesion.NatGateway.Hosting;

internal sealed class NatGatewayControlPlaneEndpointService : IHostService, IDisposable
{
    private X509Certificate2? _serverCertificate;
    private X509Certificate2Collection _serverCertificateChain = new();
    private readonly WebApplication _application;


    internal NatGatewayControlPlaneEndpointService(
        Uri endpoint,
        IResourceControlPlane controlPlane,
        ResourceContext resourceContext,
        NatGatewayApplicationContext applicationContext)
    {
        Uri.ThrowIfNotEndpoint(endpoint);
        ArgumentNullException.ThrowIfNull(controlPlane);
        ArgumentNullException.ThrowIfNull(resourceContext);
        ArgumentNullException.ThrowIfNull(applicationContext);
        if (!string.Equals(endpoint.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The NatGateway control-plane endpoint 'http' requires http or https.");
        }

        IPAddress address = ResolveBindAddress(endpoint.IdnHost);
        // Parameterless construction deliberately avoids resource registration on this private host.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        if (string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            if (resourceContext is not ResourceContext certificateContext ||
                !certificateContext.TryGetEndpointCertificate("http", out _serverCertificate, out _serverCertificateChain))
            {
                throw new InvalidOperationException("The NatGateway https endpoint 'http' requires its certificate Secret mount (default 'tls').");
            }
            SslStreamCertificateContext certificate = SslStreamCertificateContext.Create(_serverCertificate, _serverCertificateChain, offline: true);
            builder.Server.UseServer(options => options.UseHttp1s(
                tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port),
                new TlsServerOptions { AuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificateContext = certificate } }));
        }
        else
        {
            builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port)));
        }
        _application = builder.Build();
        IWebApplicationPipelineBuilder pipeline = _application;
        pipeline.UseResourceControlPlane(controlPlane, resourceContext,
            () => applicationContext.State is HostState.Started);
    }

    public ServiceId Id { get; } = ServiceId.New();

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        ((IHost)_application).StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        ((IHost)_application).StopAsync(cancellationToken);

    public void Dispose()
    {
        ((IDisposable)_application).Dispose();
        _serverCertificate?.Dispose();
        foreach (X509Certificate2 certificate in _serverCertificateChain)
        {
            certificate.Dispose();
        }

    }

    private static IPAddress ResolveBindAddress(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.TryParse(host, out IPAddress? address)
                ? address
                : throw new InvalidOperationException($"The NatGateway endpoint host '{host}' is not a bindable IP address.");

}

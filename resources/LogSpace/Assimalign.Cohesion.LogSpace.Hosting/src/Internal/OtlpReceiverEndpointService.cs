using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.ControlPlane;
using Assimalign.Cohesion.Web.Hosting;

namespace Assimalign.Cohesion.LogSpace.Hosting;

internal sealed class OtlpReceiverEndpointService : IHostService, IDisposable
{
    private readonly WebApplication _application;
    private X509Certificate2? _serverCertificate;
    private X509Certificate2[] _serverCertificateChain = [];

    internal OtlpReceiverEndpointService(
        Uri endpoint,
        LogSegmentStore store,
        ResourceContext resourceContext,
        LogSpaceApplicationContext applicationContext)
    {
        Uri.ThrowIfNotEndpoint(endpoint);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resourceContext);
        ArgumentNullException.ThrowIfNull(applicationContext);
        if (!string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The LogSpace OTLP endpoint 'otlp' requires https.");
        }

        IPAddress address = ResolveBindAddress(endpoint.IdnHost);
        // Parameterless construction deliberately avoids resource registration on this private host.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        SslStreamCertificateContext certificate;
        if (resourceContext.TryGetEndpointCertificate("otlp", out _serverCertificate, out X509Certificate2Collection chain))
        {
            _serverCertificateChain = new X509Certificate2[chain.Count];
            chain.CopyTo(_serverCertificateChain, 0);
            certificate = SslStreamCertificateContext.Create(_serverCertificate, chain, offline: true);
        }
        else if (string.Equals(resourceContext.EnvironmentName, AppEnvironment.Keys.Local, StringComparison.OrdinalIgnoreCase) && IPAddress.IsLoopback(address))
        {
            _serverCertificate = resourceContext.CreateDevelopmentEndpointCertificate(endpoint.IdnHost);
            certificate = SslStreamCertificateContext.Create(_serverCertificate, null, offline: true);
        }
        else
        {
            throw new InvalidOperationException("LogSpace requires its certificate Secret mount (default 'tls') outside loopback Local.");
        }
        builder.Server.UseServer(options => options.UseHttp1s(
            tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port),
            new TlsServerOptions
            {
                AuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificateContext = certificate },
            // The middleware owns the bounded body read and 413 response. A post-dispatch
            // transport limit currently closes the connection without sending that status.
            }, http => http.Limits.MaxRequestBodySize = null));
        _application = builder.Build();
        IWebApplicationPipelineBuilder pipeline = _application;
        pipeline.Use(_ => context => LogSpaceHttp.IngestAsync(context, resourceContext, store));
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
        foreach (X509Certificate2 certificate in _serverCertificateChain) { certificate.Dispose(); }
    }

    private static IPAddress ResolveBindAddress(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.TryParse(host, out IPAddress? address)
                ? address
                : throw new InvalidOperationException($"The LogSpace endpoint host '{host}' is not a bindable IP address.");


}

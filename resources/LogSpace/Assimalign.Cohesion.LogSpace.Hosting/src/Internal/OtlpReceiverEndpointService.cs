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
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;

namespace Assimalign.Cohesion.LogSpace.Hosting.Internal;

[global::System.Diagnostics.DebuggerDisplay("OtlpReceiverEndpointService {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class OtlpReceiverEndpointService : IHostService, IDisposable
{
    private readonly WebApplication _application;
    private X509Certificate2? _serverCertificate;
    private X509Certificate2[] _serverCertificateChain = [];

    internal OtlpReceiverEndpointService(
        Uri endpoint,
        LogSegmentStore store,
        X509Certificate2 serverCertificate,
        X509Certificate2Collection serverCertificateChain,
        LogSpaceApplicationContext applicationContext)
    {
        Uri.ThrowIfNotEndpoint(endpoint);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serverCertificate);
        ArgumentNullException.ThrowIfNull(serverCertificateChain);
        ArgumentNullException.ThrowIfNull(applicationContext);
        if (!string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The LogSpace OTLP endpoint 'otlp' requires https.");
        }

        IPAddress address = ResolveBindAddress(endpoint.IdnHost);
        // Parameterless construction deliberately avoids resource registration on this private host.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // The caller supplies the TLS material; the service owns it from here and disposes it.
        _serverCertificate = serverCertificate;
        _serverCertificateChain = new X509Certificate2[serverCertificateChain.Count];
        serverCertificateChain.CopyTo(_serverCertificateChain, 0);
        SslStreamCertificateContext certificate = SslStreamCertificateContext.Create(
            serverCertificate, serverCertificateChain, offline: true);
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
        pipeline.Use(_ => context => LogSpaceHttp.IngestAsync(context, store));
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



    private sealed class DebugView
    {
        private readonly OtlpReceiverEndpointService _target;

        public DebugView(OtlpReceiverEndpointService value)
        {
            _target = value;
        }

        public ServiceId Id => _target.Id;
    }
}

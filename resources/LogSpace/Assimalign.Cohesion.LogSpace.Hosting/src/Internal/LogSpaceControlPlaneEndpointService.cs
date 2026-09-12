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

internal sealed class LogSpaceControlPlaneEndpointService : IHostService, IDisposable
{
    private readonly WebApplication _application;
    private X509Certificate2? _serverCertificate;
    private X509Certificate2[] _serverCertificateChain = [];

    internal LogSpaceControlPlaneEndpointService(
        Uri endpoint,
        IResourceControlPlane controlPlane,
        ResourceContext resourceContext,
        LogSpaceApplicationContext applicationContext)
    {
        Uri.ThrowIfNotEndpoint(endpoint);
        ArgumentNullException.ThrowIfNull(controlPlane);
        ArgumentNullException.ThrowIfNull(resourceContext);
        ArgumentNullException.ThrowIfNull(applicationContext);
        if (!string.Equals(endpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The LogSpace control-plane endpoint 'query' requires https.");
        }

        IPAddress address = ResolveBindAddress(endpoint.IdnHost);
        // Parameterless construction deliberately avoids resource registration on this private host.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        SslStreamCertificateContext certificate = resourceContext.Mounts.TryGetValue("tls", out ResourceMount? mount)
            ? CreateMountedServerCertificateContext(mount)
            : CreateDevelopmentServerCertificateContext(endpoint.IdnHost);
        builder.Server.UseServer(options => options.UseHttp1s(
            tcp => tcp.EndPoint = new IPEndPoint(address, endpoint.Port),
            new TlsServerOptions
            {
                AuthenticationOptions = new SslServerAuthenticationOptions { ServerCertificateContext = certificate },
            }));
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
        foreach (X509Certificate2 certificate in _serverCertificateChain) { certificate.Dispose(); }
    }

    private static IPAddress ResolveBindAddress(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.TryParse(host, out IPAddress? address)
                ? address
                : throw new InvalidOperationException($"The LogSpace endpoint host '{host}' is not a bindable IP address.");
    private SslStreamCertificateContext CreateDevelopmentServerCertificateContext(string host)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            names.AddIpAddress(address);
        }
        else
        {
            names.AddDnsName(host);
        }

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
            critical: false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
            request.PublicKey,
            critical: false));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
        _serverCertificate = X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pkcs12),
            null);
        return SslStreamCertificateContext.Create(_serverCertificate, null, offline: true);
    }

    private SslStreamCertificateContext CreateMountedServerCertificateContext(ResourceMount mount)
    {
        byte[] content = mount.ReadAllBytes();
        char[] pem = new char[Encoding.UTF8.GetCharCount(content)];
        X509Certificate2? loadedServerCertificate = null;
        var loadedChain = new List<X509Certificate2>();
        var parsedCertificates = new X509Certificate2Collection();
        try
        {
            Encoding.UTF8.GetChars(content, pem);
            using X509Certificate2 parsedServerCertificate = X509Certificate2.CreateFromPem(pem, pem);
            if (!parsedServerCertificate.HasPrivateKey)
            {
                throw new InvalidOperationException(
                    "The LogSpace 'tls' mount certificate does not contain a matching private key.");
            }

            byte[] pkcs12 = parsedServerCertificate.Export(X509ContentType.Pkcs12);
            try
            {
                loadedServerCertificate = X509CertificateLoader.LoadPkcs12(
                    pkcs12,
                    null);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }

            DateTime now = DateTime.UtcNow;
            if (loadedServerCertificate.NotBefore.ToUniversalTime() > now ||
                loadedServerCertificate.NotAfter.ToUniversalTime() <= now)
            {
                throw new InvalidOperationException(
                    "The LogSpace 'tls' mount certificate is not currently valid.");
            }

            parsedCertificates.ImportFromPem(pem);
            for (int index = 0; index < parsedCertificates.Count; index++)
            {
                X509Certificate2 certificate = parsedCertificates[index];
                if (!certificate.RawDataMemory.Span.SequenceEqual(
                        loadedServerCertificate.RawDataMemory.Span))
                {
                    loadedChain.Add(X509CertificateLoader.LoadCertificate(certificate.RawData));
                }
            }

            var additionalCertificates = new X509Certificate2Collection(loadedChain.ToArray());
            SslStreamCertificateContext context = SslStreamCertificateContext.Create(
                loadedServerCertificate,
                additionalCertificates,
                offline: true);
            _serverCertificate = loadedServerCertificate;
            _serverCertificateChain = loadedChain.ToArray();
            loadedServerCertificate = null;
            loadedChain.Clear();
            return context;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                "The LogSpace 'tls' resource mount must contain a PEM certificate, " +
                "matching private key, and optional certificate chain.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            Array.Clear(pem);
            loadedServerCertificate?.Dispose();
            for (int index = 0; index < loadedChain.Count; index++)
            {
                loadedChain[index].Dispose();
            }

            for (int index = 0; index < parsedCertificates.Count; index++)
            {
                parsedCertificates[index].Dispose();
            }
        }
    }


}

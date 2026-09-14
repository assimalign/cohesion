using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting.Internal;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class HttpServerConfigurationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - HTTPS configuration: Resolves the named Secret mount for each secure protocol")]
    [InlineData("Https", HttpProtocol.Http11)]
    [InlineData("Http1s", HttpProtocol.Http11)]
    [InlineData("Http2s", HttpProtocol.Http20)]
    public async Task Bind_HttpsProtocols_ShouldUseCertificateMount(string protocol, HttpProtocol expected)
    {
        var development = new ResourceContext(environmentName: "Development");
        using X509Certificate2 certificate = development.CreateDevelopmentEndpointCertificate("localhost");
        using var key = certificate.GetECDsaPrivateKey()!;
        string pem = certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem();
        var resource = new ResourceContext(mounts: new Dictionary<string, ResourceMount>
        {
            ["https-key"] = ResourceMount.FromBytes(Encoding.UTF8.GetBytes(pem)),
        });
        using IDisposable scope = ResourceRuntime.CreateScope(resource);
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Endpoints:Secure:Protocol"] = protocol,
            ["Http:Endpoints:Secure:Host"] = "localhost",
            ["Http:Endpoints:Secure:Port"] = "0",
            ["Http:Endpoints:Secure:Certificate"] = "https-key",
            ["Http:Limits:MaxRequestBodySize"] = "1024",
        });
        var options = new HttpConnectionListenerOptions();
        var owned = new List<X509Certificate2>();
        try
        {
            HttpServerConfiguration.Bind(configuration, "Http", options, owned.Add);
            await using HttpConnectionListener listener = new(options);
            listener.Protocols.ShouldBe(expected);
            owned[0].Thumbprint.ShouldBe(certificate.Thumbprint);
        }
        finally
        {
            foreach (X509Certificate2 item in owned)
            {
                item.Dispose();
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HTTPS configuration: Missing material names the endpoint and tls mount")]
    public void Bind_MissingHttpsCertificate_ShouldFailClosed()
    {
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext());
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Endpoints:Secure:Protocol"] = "Https",
            ["Http:Endpoints:Secure:Port"] = "8443",
        });
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => HttpServerConfiguration.Bind(configuration, "Http", new HttpConnectionListenerOptions()));
        error.Message.ShouldContain("Secure");
        error.Message.ShouldContain("tls");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should bind server limits from configuration")]
    public void Bind_ShouldPopulateLimits()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Limits:MaxRequestLineSize"] = "4096",
            ["Http:Limits:MaxRequestHeaderCount"] = "50",
            ["Http:Limits:MaxRequestHeadersTotalSize"] = "16384",
            ["Http:Limits:MaxRequestBodySize"] = "1048576",
            ["Http:Limits:KeepAliveTimeout"] = "00:01:00",
            ["Http:Limits:RequestHeadersTimeout"] = "00:00:15",
        });
        Http1ConnectionListenerOptions.Http1Limits limits = new();

        HttpServerConfiguration.BindLimits(configuration, HttpServerConfiguration.DefaultSectionKey, limits);

        limits.MaxRequestLineSize.ShouldBe(4096);
        limits.MaxRequestHeaderCount.ShouldBe(50);
        limits.MaxRequestHeadersTotalSize.ShouldBe(16384);
        limits.MaxRequestBodySize.ShouldBe(1048576);
        limits.KeepAliveTimeout.ShouldBe(TimeSpan.FromMinutes(1));
        limits.RequestHeadersTimeout.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should leave defaults when a section is absent")]
    public void Bind_OnEmptyConfiguration_ShouldLeaveDefaults()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>());
        Http1ConnectionListenerOptions.Http1Limits limits = new();

        HttpServerConfiguration.BindLimits(configuration, HttpServerConfiguration.DefaultSectionKey, limits);

        limits.MaxRequestLineSize.ShouldBe(8 * 1024);
        limits.MaxRequestBodySize.ShouldBe(30_000_000);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should treat 'unbounded' body size as null")]
    public void Bind_OnUnboundedBodySize_ShouldSetNull()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Limits:MaxRequestBodySize"] = "unbounded",
        });
        Http1ConnectionListenerOptions.Http1Limits limits = new();

        HttpServerConfiguration.BindLimits(configuration, HttpServerConfiguration.DefaultSectionKey, limits);

        limits.MaxRequestBodySize.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should treat 'infinite' timeout as InfiniteTimeSpan")]
    public void Bind_OnInfiniteTimeout_ShouldSetInfinite()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Limits:KeepAliveTimeout"] = "infinite",
        });
        Http1ConnectionListenerOptions.Http1Limits limits = new();

        HttpServerConfiguration.BindLimits(configuration, HttpServerConfiguration.DefaultSectionKey, limits);

        limits.KeepAliveTimeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should register HTTP/1.1 and HTTP/2 endpoints from configuration")]
    public async Task Bind_ShouldRegisterEndpoints()
    {
        // Port 0 binds an ephemeral port; the listener is constructed (which materializes the TCP
        // listener factories) but the socket only binds lazily on accept, so this stays offline.
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Endpoints:Primary:Protocol"] = "Http1",
            ["Http:Endpoints:Primary:Host"] = "127.0.0.1",
            ["Http:Endpoints:Primary:Port"] = "0",
            ["Http:Endpoints:Secondary:Protocol"] = "Http2",
            ["Http:Endpoints:Secondary:Host"] = "127.0.0.1",
            ["Http:Endpoints:Secondary:Port"] = "0",
        });
        HttpConnectionListenerOptions options = new();

        HttpServerConfiguration.Bind(configuration, HttpServerConfiguration.DefaultSectionKey, options);

        await using HttpConnectionListener listener = new(options);
        listener.Protocols.ShouldBe(HttpProtocol.Http11 | HttpProtocol.Http20);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should throw on an invalid endpoint port")]
    public void Bind_OnInvalidPort_ShouldThrow()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Endpoints:Primary:Protocol"] = "Http1",
            ["Http:Endpoints:Primary:Host"] = "127.0.0.1",
            ["Http:Endpoints:Primary:Port"] = "not-a-port",
        });
        HttpConnectionListenerOptions options = new();

        Should.Throw<InvalidOperationException>(
            () => HttpServerConfiguration.Bind(configuration, HttpServerConfiguration.DefaultSectionKey, options));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should throw on an unsupported endpoint protocol")]
    public void Bind_OnUnsupportedProtocol_ShouldThrow()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Endpoints:Primary:Protocol"] = "Gopher",
            ["Http:Endpoints:Primary:Host"] = "127.0.0.1",
            ["Http:Endpoints:Primary:Port"] = "8080",
        });
        HttpConnectionListenerOptions options = new();

        Should.Throw<InvalidOperationException>(
            () => HttpServerConfiguration.Bind(configuration, HttpServerConfiguration.DefaultSectionKey, options));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should throw on an unparseable limit value")]
    public void Bind_OnUnparseableLimit_ShouldThrow()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Limits:MaxRequestLineSize"] = "not-an-int",
        });
        HttpConnectionListenerOptions options = new();

        Should.Throw<InvalidOperationException>(
            () => HttpServerConfiguration.Bind(configuration, HttpServerConfiguration.DefaultSectionKey, options));
    }

    private static IConfiguration BuildConfiguration(IDictionary<string, string?> values)
    {
        ConfigurationManager manager = new();
        manager.AddProvider(new SeededConfigurationProvider(values));
        return manager;
    }

    private sealed class SeededConfigurationProvider : ConfigurationProvider
    {
        private readonly IDictionary<string, string?> _values;

        public SeededConfigurationProvider(IDictionary<string, string?> values)
        {
            _values = values;
        }

        public override string Name => "Seeded";

        protected override Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default)
        {
            foreach (KeyValuePair<string, string?> value in _values)
            {
                entries[Path.Parse(value.Key)] = value.Value;
            }

            return Task.CompletedTask;
        }
    }
}

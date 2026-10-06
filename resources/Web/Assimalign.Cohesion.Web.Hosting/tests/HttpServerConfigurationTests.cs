using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class HttpServerConfigurationTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - HTTPS configuration: Resolves the named Secret mount for each secure protocol")]
    [InlineData("Https", HttpProtocol.Http11 | HttpProtocol.Http20)]
    [InlineData("Http1s", HttpProtocol.Http11)]
    [InlineData("Http2s", HttpProtocol.Http20)]
    public async Task Bind_HttpsProtocols_ShouldUseCertificateMount(string protocol, HttpProtocol expected)
    {
        var local = new ResourceContext(environmentName: AppEnvironment.Keys.Local);
        using X509Certificate2 certificate = local.CreateDevelopmentEndpointCertificate("localhost");
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

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Default endpoints: the binder registers the development endpoint when none is configured")]
    public async Task BindOrDefault_NoEndpoints_ShouldRegisterDevelopmentEndPoint()
    {
        // Arrange
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>());
        var options = new HttpConnectionListenerOptions();

        // Act
        HttpServerConfiguration.BindOrDefault(configuration, HttpServerConfiguration.DefaultSectionKey, options, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));

        // Assert
        await using HttpConnectionListener listener = new(options);
        listener.Protocols.ShouldBe(HttpProtocol.Http11);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Default endpoints: the binder registers only configured endpoints when present")]
    public async Task BindOrDefault_ConfiguredEndpoint_ShouldNotRegisterDevelopmentEndPoint()
    {
        // Arrange — an HTTP/2 endpoint; a development endpoint would add HTTP/1.1 to the protocols.
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Endpoints:Main:Protocol"] = "Http2",
            ["Http:Endpoints:Main:Host"] = "127.0.0.1",
            ["Http:Endpoints:Main:Port"] = "0",
        });
        var options = new HttpConnectionListenerOptions();

        // Act
        HttpServerConfiguration.BindOrDefault(configuration, HttpServerConfiguration.DefaultSectionKey, options, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));

        // Assert
        await using HttpConnectionListener listener = new(options);
        listener.Protocols.ShouldBe(HttpProtocol.Http20);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should register an HTTP/3 endpoint and advertise it through Alt-Svc")]
    public async Task Bind_Http3Endpoint_ShouldRegisterHttp3AndEnableAltSvc()
    {
        // Arrange — an h3 endpoint beside an https one; the QUIC listener is created unbound here.
        using CertificateFiles files = CertificateFiles.Create();
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Endpoints:Secure:Protocol"] = "Https",
            ["Http:Endpoints:Secure:Port"] = "0",
            ["Http:Endpoints:Secure:Certificate:Path"] = files.PemCertificatePath,
            ["Http:Endpoints:Secure:Certificate:KeyPath"] = files.PemKeyPath,
            ["Http:Endpoints:Quic:Protocol"] = "Http3",
            ["Http:Endpoints:Quic:Port"] = "0",
            ["Http:Endpoints:Quic:Certificate:Path"] = files.PemCertificatePath,
            ["Http:Endpoints:Quic:Certificate:KeyPath"] = files.PemKeyPath,
        });
        var options = new HttpConnectionListenerOptions();
        var owned = new List<X509Certificate2>();

        try
        {
            // Act
            HttpServerConfiguration.Bind(configuration, "Http", options, owned.Add, files.Directory);

            // Assert
            options.AltServiceAdvertisement.Enabled.ShouldBeTrue();
            await using HttpConnectionListener listener = new(options);
            listener.Protocols.ShouldBe(HttpProtocol.Http11 | HttpProtocol.Http20 | HttpProtocol.Http30);
        }
        finally
        {
            DisposeAll(owned);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should leave Alt-Svc off when no HTTP/3 endpoint is configured")]
    public void Bind_WithoutHttp3Endpoint_ShouldLeaveAltSvcDisabled()
    {
        // Arrange
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Endpoints:Primary:Protocol"] = "Http1",
            ["Http:Endpoints:Primary:Port"] = "0",
        });
        var options = new HttpConnectionListenerOptions();

        // Act
        HttpServerConfiguration.Bind(configuration, "Http", options);

        // Assert
        options.AltServiceAdvertisement.Enabled.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - HTTPS configuration: Loads the certificate from a PEM or PFX file")]
    [InlineData("pem-with-key-file")]
    [InlineData("pem-bundle")]
    [InlineData("pem-encrypted-key")]
    [InlineData("pfx")]
    [InlineData("pfx-relative")]
    public async Task Bind_CertificateFile_ShouldLoadLeafWithPrivateKey(string source)
    {
        // Arrange
        using CertificateFiles files = CertificateFiles.Create();
        Dictionary<string, string?> values = new()
        {
            ["Http:Endpoints:Secure:Protocol"] = "Https",
            ["Http:Endpoints:Secure:Port"] = "0",
        };
        switch (source)
        {
            case "pem-with-key-file":
                values["Http:Endpoints:Secure:Certificate:Path"] = files.PemCertificatePath;
                values["Http:Endpoints:Secure:Certificate:KeyPath"] = files.PemKeyPath;
                break;
            case "pem-bundle":
                values["Http:Endpoints:Secure:Certificate:Path"] = files.PemBundlePath;
                break;
            case "pem-encrypted-key":
                values["Http:Endpoints:Secure:Certificate:Path"] = files.PemCertificatePath;
                values["Http:Endpoints:Secure:Certificate:KeyPath"] = files.EncryptedPemKeyPath;
                values["Http:Endpoints:Secure:Certificate:Password"] = CertificateFiles.Password;
                break;
            case "pfx":
                values["Http:Endpoints:Secure:Certificate:Path"] = files.PfxPath;
                values["Http:Endpoints:Secure:Certificate:Password"] = CertificateFiles.Password;
                break;
            case "pfx-relative":
                // A relative path resolves against the content root.
                values["Http:Endpoints:Secure:Certificate:Path"] = System.IO.Path.GetFileName(files.PfxPath);
                values["Http:Endpoints:Secure:Certificate:Password"] = CertificateFiles.Password;
                break;
        }

        IConfiguration configuration = BuildConfiguration(values);
        var options = new HttpConnectionListenerOptions();
        var owned = new List<X509Certificate2>();

        try
        {
            // Act
            HttpServerConfiguration.Bind(configuration, "Http", options, owned.Add, files.Directory);

            // Assert — the leaf is owned first, carries its key, and the endpoint serves h1 and h2.
            owned[0].Thumbprint.ShouldBe(files.Certificate.Thumbprint);
            owned[0].HasPrivateKey.ShouldBeTrue();
            await using HttpConnectionListener listener = new(options);
            listener.Protocols.ShouldBe(HttpProtocol.Http11 | HttpProtocol.Http20);
        }
        finally
        {
            DisposeAll(owned);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HTTPS configuration: A certificate section without a Path names the endpoint")]
    public void Bind_CertificateSectionWithoutPath_ShouldThrow()
    {
        // Arrange
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Endpoints:Secure:Protocol"] = "Https",
            ["Http:Endpoints:Secure:Port"] = "0",
            ["Http:Endpoints:Secure:Certificate:Password"] = "unused",
        });

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => HttpServerConfiguration.Bind(configuration, "Http", new HttpConnectionListenerOptions()));

        // Assert
        error.Message.ShouldContain("Secure");
        error.Message.ShouldContain("Path");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HTTPS configuration: An unreadable certificate file names the endpoint and the file")]
    public void Bind_MissingCertificateFile_ShouldThrowNamingEndpointAndPath()
    {
        // Arrange
        string missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cohesion-missing-{Guid.NewGuid():N}.pfx");
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Endpoints:Secure:Protocol"] = "Http2s",
            ["Http:Endpoints:Secure:Port"] = "0",
            ["Http:Endpoints:Secure:Certificate:Path"] = missing,
        });

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => HttpServerConfiguration.Bind(configuration, "Http", new HttpConnectionListenerOptions()));

        // Assert
        error.Message.ShouldContain("Secure");
        error.Message.ShouldContain(missing);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should bind the HTTP/2 limits from configuration")]
    public void BindHttp2Limits_ShouldPopulateLimits()
    {
        // Arrange
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Limits:Http2:MaxStreamsPerConnection"] = "50",
            ["Http:Limits:Http2:MaxRequestHeaderListSize"] = "8192",
            ["Http:Limits:Http2:MaxResetStreamsPerWindow"] = "20",
            ["Http:Limits:Http2:MaxSettingsFramesPerWindow"] = "10",
            ["Http:Limits:Http2:MaxPingFramesPerWindow"] = "15",
            ["Http:Limits:Http2:FloodDetectionWindow"] = "00:00:10",
        });
        Http2ConnectionListenerOptions.Http2Limits limits = new();

        // Act
        HttpServerConfiguration.BindHttp2Limits(configuration, HttpServerConfiguration.DefaultSectionKey, limits);

        // Assert
        limits.MaxStreamsPerConnection.ShouldBe(50);
        limits.MaxRequestHeaderListSize.ShouldBe(8192);
        limits.MaxResetStreamsPerWindow.ShouldBe(20);
        limits.MaxSettingsFramesPerWindow.ShouldBe(10);
        limits.MaxPingFramesPerWindow.ShouldBe(15);
        limits.FloodDetectionWindow.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should throw on an unparseable HTTP/2 limit")]
    public void BindHttp2Limits_OnUnparseableValue_ShouldThrow()
    {
        // Arrange
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Limits:Http2:MaxStreamsPerConnection"] = "lots",
        });
        HttpConnectionListenerOptions options = new();

        // Act / Assert — parsed eagerly, even with no endpoint to consume it.
        Should.Throw<InvalidOperationException>(
            () => HttpServerConfiguration.Bind(configuration, HttpServerConfiguration.DefaultSectionKey, options));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should hand the configured connection cap to the server")]
    public void BindConnectionLimit_WhenConfigured_ShouldInvokeCallback()
    {
        // Arrange
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Limits:MaxConcurrentConnections"] = "250",
        });
        int? cap = null;

        // Act
        HttpServerConfiguration.BindConnectionLimit(configuration, HttpServerConfiguration.DefaultSectionKey, value => cap = value);

        // Assert
        cap.ShouldBe(250);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - HttpServerConfiguration: Should reject a connection cap that is not a positive integer")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("many")]
    public void BindConnectionLimit_OnInvalidValue_ShouldThrow(string value)
    {
        // Arrange
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Limits:MaxConcurrentConnections"] = value,
        });

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => HttpServerConfiguration.BindConnectionLimit(configuration, HttpServerConfiguration.DefaultSectionKey, _ => { }));

        // Assert
        error.Message.ShouldContain("MaxConcurrentConnections");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseConfiguration: Should cap the default server with the configured connection limit")]
    public async Task UseConfiguration_WithConnectionLimit_ShouldCapTheDefaultServer()
    {
        // Arrange
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Limits:MaxConcurrentConnections"] = "40",
        });
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseConfiguration(configuration);
        await using WebApplication application = builder.Build();

        // Act
        IWebApplicationServer server = application.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();

        // Assert
        server.ShouldBeOfType<WebApplicationServer>().MaxConcurrentConnections.ShouldBe(40);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseConfiguration: An explicit LimitConcurrentConnections takes precedence over the configured cap")]
    public async Task UseConfiguration_WithExplicitLimit_ShouldPreferExplicitLimit()
    {
        // Arrange
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Http:Limits:MaxConcurrentConnections"] = "40",
        });
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.LimitConcurrentConnections(3).UseConfiguration(configuration);
        await using WebApplication application = builder.Build();

        // Act
        IWebApplicationServer server = application.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();

        // Assert
        server.ShouldBeOfType<WebApplicationServer>().MaxConcurrentConnections.ShouldBe(3);
    }

    private static void DisposeAll(List<X509Certificate2> certificates)
    {
        foreach (X509Certificate2 certificate in certificates)
        {
            certificate.Dispose();
        }
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

using System;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http.Connections;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// Binds HTTP server listener endpoints and per-endpoint limits from a Cohesion
/// <see cref="IConfiguration"/> section onto an <see cref="HttpConnectionListenerOptions"/> at
/// builder time.
/// </summary>
/// <remarks>
/// <para>
/// The binding is fully explicit and AOT-safe: every value is read by its known path and parsed
/// with the invariant culture. There is no reflection, no dynamic member discovery, and no
/// <c>Microsoft.Extensions.*</c> dependency. A value that is present but unparseable fails loudly
/// so a mis-typed security limit is never silently ignored; an absent value leaves the built-in
/// default in place.
/// </para>
/// <para>
/// The expected section shape mirrors the Kestrel <c>appsettings</c> layout (a section with an
/// <c>Endpoints</c> map and a <c>Limits</c> object):
/// </para>
/// <code>
/// "Http": {
///   "Endpoints": {
///     "Public":   { "Protocol": "Https", "Host": "0.0.0.0", "Port": 443,
///                   "Certificate": { "Path": "certs/site.pem", "KeyPath": "certs/site.key" } },
///     "Quic":     { "Protocol": "Http3", "Host": "0.0.0.0", "Port": 443,
///                   "Certificate": { "Path": "certs/site.pfx", "Password": "..." } },
///     "Internal": { "Protocol": "Http1", "Host": "localhost", "Port": 8080 }
///   },
///   "Limits": {
///     "MaxConcurrentConnections":  1000,
///     "MaxRequestLineSize":        8192,
///     "MaxRequestHeaderCount":     100,
///     "MaxRequestHeadersTotalSize": 32768,
///     "MaxRequestBodySize":        30000000,
///     "KeepAliveTimeout":          "00:02:10",
///     "RequestHeadersTimeout":     "00:00:30",
///     "Http2": { "MaxStreamsPerConnection": 100, "MaxRequestHeaderListSize": 16384 }
///   }
/// }
/// </code>
/// <para>
/// <c>Protocol</c> names the registration verb it binds to: <c>Http1</c> (the default) and
/// <c>Http2</c> are cleartext; <c>Http1s</c> and <c>Http2s</c> serve one protocol over TLS;
/// <c>Https</c> serves HTTP/2 and HTTP/1.1 over TLS, chosen per connection through ALPN; and
/// <c>Http3</c> serves HTTP/3 over QUIC. A TLS endpoint's <c>Certificate</c> is either a scalar
/// naming the Secret mount that carries a PEM bundle (the default mount is <c>tls</c>) or a section
/// naming a file: <c>Path</c> to a PEM or PKCS#12 (PFX) file, with an optional <c>KeyPath</c> for a
/// separate PEM key and an optional <c>Password</c> for an encrypted key or a protected PFX. A
/// relative path resolves against the content root. A TLS endpoint's <c>ClientCertificateMode</c>
/// (<c>NoCertificate</c>, the default; <c>AllowCertificate</c>; <c>RequireCertificate</c>) requests
/// client certificates in the handshake and accepts one only when it chains to a root the machine
/// trusts; a cleartext endpoint that declares one is refused.
/// </para>
/// <para>
/// Limits are per HTTP version on the transport
/// (<see cref="Http1ConnectionListenerOptions.Http1Limits"/> /
/// <see cref="Http2ConnectionListenerOptions.Http2Limits"/> /
/// <see cref="Http3ConnectionListenerOptions.Http3Limits"/> deriving from the shared
/// <see cref="HttpConnectionListenerLimits"/>), so the single <c>Limits</c> section is applied to
/// every endpoint this binder registers: HTTP/1.1 endpoints receive the HTTP/1.1 keys; HTTP/2
/// endpoints receive the shared keys (<c>MaxRequestBodySize</c>, <c>KeepAliveTimeout</c>,
/// <c>RequestHeadersTimeout</c>) and the <c>Limits:Http2</c> keys; HTTP/3 endpoints receive the
/// shared keys. An <c>Https</c> endpoint applies the HTTP/1.1 keys to the connections it serves
/// HTTP/1.1 and the HTTP/2 keys to those it serves HTTP/2. <c>MaxConcurrentConnections</c> caps the
/// default server rather than an endpoint. The section is parsed eagerly — before any endpoint is
/// registered — so an unparseable value fails loudly even when no endpoint consumes it.
/// </para>
/// </remarks>
internal static class HttpServerConfiguration
{
    /// <summary>
    /// The default configuration section key the server binds from when the caller does not
    /// specify one.
    /// </summary>
    public const string DefaultSectionKey = "Http";

    /// <summary>
    /// The endpoint an entry-point application (<c>WebApplication.CreateBuilder(args)</c>) binds
    /// when neither its code nor its configuration declares a listener: HTTP/1.1 on
    /// <c>127.0.0.1:5000</c>, loopback only, so an unconfigured application is never exposed
    /// beyond the machine.
    /// </summary>
    public static readonly IPEndPoint DevelopmentEndPoint = new(IPAddress.Loopback, 5000);

    /// <summary>
    /// Binds the endpoints declared under <paramref name="sectionKey"/>, or — when the section
    /// declares none — HTTP/1.1 on <paramref name="developmentEndPoint"/>. The section's
    /// <c>Limits</c> apply either way.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <param name="sectionKey">The root section key (for example <c>"Http"</c>).</param>
    /// <param name="options">The listener options to populate.</param>
    /// <param name="developmentEndPoint">The endpoint bound when no endpoint is configured.</param>
    /// <param name="ownCertificate">Receives certificate ownership for disposal with the host.</param>
    /// <param name="contentRootPath">The directory a relative certificate path resolves against; the application base directory when <see langword="null"/>.</param>
    /// <param name="limitConcurrentConnections">Receives the configured <c>Limits:MaxConcurrentConnections</c>, when one is configured.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a configured value cannot be parsed or a certificate cannot be loaded.</exception>
    public static void BindOrDefault(
        IConfiguration configuration,
        string sectionKey,
        HttpConnectionListenerOptions options,
        IPEndPoint developmentEndPoint,
        Action<X509Certificate2>? ownCertificate = null,
        string? contentRootPath = null,
        Action<int>? limitConcurrentConnections = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(sectionKey);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(developmentEndPoint);

        BoundLimits limits = BindAllLimits(configuration, sectionKey, limitConcurrentConnections);

        if (HasEndpoints(configuration, sectionKey))
        {
            BindEndpoints(configuration, sectionKey, options, limits, ownCertificate, contentRootPath);
            return;
        }

        options.UseHttp1(
            () => TcpConnectionListener.Create(tcp => tcp.EndPoint = developmentEndPoint),
            http1 => CopyHttp1Limits(limits.Http1, http1.Limits));
    }

    private static bool HasEndpoints(IConfiguration configuration, string sectionKey)
    {
        IConfigurationSection? endpoints = configuration.GetSection($"{sectionKey}:Endpoints");
        if (endpoints is null)
        {
            return false;
        }

        foreach (IConfigurationEntry child in endpoints.GetChildren())
        {
            if (child is IConfigurationSection)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Binds the endpoints and limits declared under <paramref name="sectionKey"/> onto
    /// <paramref name="options"/>.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <param name="sectionKey">The root section key (for example <c>"Http"</c>).</param>
    /// <param name="options">The listener options to populate.</param>
    /// <param name="ownCertificate">Receives certificate ownership for disposal with the host.</param>
    /// <param name="contentRootPath">The directory a relative certificate path resolves against; the application base directory when <see langword="null"/>.</param>
    /// <param name="limitConcurrentConnections">Receives the configured <c>Limits:MaxConcurrentConnections</c>, when one is configured.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a configured value cannot be parsed or a certificate cannot be loaded.</exception>
    public static void Bind(
        IConfiguration configuration,
        string sectionKey,
        HttpConnectionListenerOptions options,
        Action<X509Certificate2>? ownCertificate = null,
        string? contentRootPath = null,
        Action<int>? limitConcurrentConnections = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(sectionKey);
        ArgumentNullException.ThrowIfNull(options);

        // Parse the Limits section eagerly into templates so an unparseable value fails loudly
        // even when no endpoint is declared; each registered endpoint then copies the bound
        // values into its own per-registration limits.
        BoundLimits limits = BindAllLimits(configuration, sectionKey, limitConcurrentConnections);
        BindEndpoints(configuration, sectionKey, options, limits, ownCertificate, contentRootPath);
    }

    /// <summary>
    /// Binds the <c>Limits</c> object under <paramref name="sectionKey"/> onto
    /// <paramref name="limits"/>. Absent values leave the built-in defaults in place; present but
    /// unparseable values throw.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <param name="sectionKey">The root section key (for example <c>"Http"</c>).</param>
    /// <param name="limits">The HTTP/1.1 limits to populate (the superset the section models).</param>
    /// <exception cref="InvalidOperationException">Thrown when a configured value cannot be parsed.</exception>
    internal static void BindLimits(IConfiguration configuration, string sectionKey, Http1ConnectionListenerOptions.Http1Limits limits)
    {
        if (TryGetInt(configuration, $"{sectionKey}:Limits:MaxRequestLineSize", out int maxRequestLineSize))
        {
            limits.MaxRequestLineSize = maxRequestLineSize;
        }

        if (TryGetInt(configuration, $"{sectionKey}:Limits:MaxRequestHeaderCount", out int maxRequestHeaderCount))
        {
            limits.MaxRequestHeaderCount = maxRequestHeaderCount;
        }

        if (TryGetInt(configuration, $"{sectionKey}:Limits:MaxRequestHeadersTotalSize", out int maxRequestHeadersTotalSize))
        {
            limits.MaxRequestHeadersTotalSize = maxRequestHeadersTotalSize;
        }

        string? maxRequestBodySize = GetString(configuration, $"{sectionKey}:Limits:MaxRequestBodySize");
        if (maxRequestBodySize is not null)
        {
            limits.MaxRequestBodySize = ParseMaxRequestBodySize(maxRequestBodySize);
        }

        if (TryGetTimeout(configuration, $"{sectionKey}:Limits:KeepAliveTimeout", out TimeSpan keepAliveTimeout))
        {
            limits.KeepAliveTimeout = keepAliveTimeout;
        }

        if (TryGetTimeout(configuration, $"{sectionKey}:Limits:RequestHeadersTimeout", out TimeSpan requestHeadersTimeout))
        {
            limits.RequestHeadersTimeout = requestHeadersTimeout;
        }
    }

    /// <summary>
    /// Binds the <c>Limits:Http2</c> object under <paramref name="sectionKey"/> onto
    /// <paramref name="limits"/>: the HTTP/2 abuse-mitigation caps. Absent values leave the built-in
    /// defaults in place; present but unparseable values throw.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <param name="sectionKey">The root section key (for example <c>"Http"</c>).</param>
    /// <param name="limits">The HTTP/2 limits to populate.</param>
    /// <exception cref="InvalidOperationException">Thrown when a configured value cannot be parsed.</exception>
    internal static void BindHttp2Limits(IConfiguration configuration, string sectionKey, Http2ConnectionListenerOptions.Http2Limits limits)
    {
        if (TryGetInt(configuration, $"{sectionKey}:Limits:Http2:MaxStreamsPerConnection", out int maxStreamsPerConnection))
        {
            limits.MaxStreamsPerConnection = maxStreamsPerConnection;
        }

        if (TryGetInt(configuration, $"{sectionKey}:Limits:Http2:MaxRequestHeaderListSize", out int maxRequestHeaderListSize))
        {
            limits.MaxRequestHeaderListSize = maxRequestHeaderListSize;
        }

        if (TryGetInt(configuration, $"{sectionKey}:Limits:Http2:MaxResetStreamsPerWindow", out int maxResetStreamsPerWindow))
        {
            limits.MaxResetStreamsPerWindow = maxResetStreamsPerWindow;
        }

        if (TryGetInt(configuration, $"{sectionKey}:Limits:Http2:MaxSettingsFramesPerWindow", out int maxSettingsFramesPerWindow))
        {
            limits.MaxSettingsFramesPerWindow = maxSettingsFramesPerWindow;
        }

        if (TryGetInt(configuration, $"{sectionKey}:Limits:Http2:MaxPingFramesPerWindow", out int maxPingFramesPerWindow))
        {
            limits.MaxPingFramesPerWindow = maxPingFramesPerWindow;
        }

        if (TryGetTimeout(configuration, $"{sectionKey}:Limits:Http2:FloodDetectionWindow", out TimeSpan floodDetectionWindow))
        {
            limits.FloodDetectionWindow = floodDetectionWindow;
        }
    }

    /// <summary>
    /// Reads <c>Limits:MaxConcurrentConnections</c> under <paramref name="sectionKey"/> and hands it to
    /// <paramref name="limitConcurrentConnections"/>. Absent leaves the server unlimited; a value that is
    /// not a positive integer throws.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <param name="sectionKey">The root section key (for example <c>"Http"</c>).</param>
    /// <param name="limitConcurrentConnections">Receives the configured cap.</param>
    /// <exception cref="InvalidOperationException">Thrown when the configured value is not a positive integer.</exception>
    internal static void BindConnectionLimit(IConfiguration configuration, string sectionKey, Action<int>? limitConcurrentConnections)
    {
        string path = $"{sectionKey}:Limits:MaxConcurrentConnections";
        if (!TryGetInt(configuration, path, out int maxConcurrentConnections))
        {
            return;
        }

        if (maxConcurrentConnections <= 0)
        {
            throw new InvalidOperationException(
                $"The configured value at '{path}' ('{maxConcurrentConnections}') must be greater than zero.");
        }

        limitConcurrentConnections?.Invoke(maxConcurrentConnections);
    }

    private static BoundLimits BindAllLimits(IConfiguration configuration, string sectionKey, Action<int>? limitConcurrentConnections)
    {
        BoundLimits limits = new();
        BindLimits(configuration, sectionKey, limits.Http1);
        BindHttp2Limits(configuration, sectionKey, limits.Http2);
        BindConnectionLimit(configuration, sectionKey, limitConcurrentConnections);
        return limits;
    }

    private static void BindEndpoints(
        IConfiguration configuration,
        string sectionKey,
        HttpConnectionListenerOptions options,
        BoundLimits limits,
        Action<X509Certificate2>? ownCertificate,
        string? contentRootPath)
    {
        IConfigurationSection? endpoints = configuration.GetSection($"{sectionKey}:Endpoints");
        if (endpoints is null)
        {
            return;
        }

        bool http3 = false;
        foreach (IConfigurationEntry child in endpoints.GetChildren())
        {
            if (child is IConfigurationSection endpoint)
            {
                http3 |= BindEndpoint(endpoint, options, limits, ownCertificate, contentRootPath) == EndpointProtocol.Http3;
            }
        }

        if (http3)
        {
            // RFC 7838 / RFC 9114 §3.1 — Alt-Svc is how a client on HTTP/1.1 or HTTP/2 discovers the h3
            // endpoint, so configuring one turns the advertisement on. The listener emits it only when a
            // stream endpoint exists to carry it; a later UseServer callback can still turn it off.
            options.AltServiceAdvertisement.Enabled = true;
        }
    }

    private static EndpointProtocol BindEndpoint(
        IConfigurationSection endpoint,
        HttpConnectionListenerOptions options,
        BoundLimits limits,
        Action<X509Certificate2>? ownCertificate,
        string? contentRootPath)
    {
        string endpointName = endpoint.Key.ToString();
        EndpointProtocol protocol = ParseProtocol(endpointName, GetString(endpoint, "Protocol"));
        string? host = GetString(endpoint, "Host");
        string? portText = GetString(endpoint, "Port");

        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port is < 0 or > 65535)
        {
            throw new InvalidOperationException(
                $"The HTTP endpoint '{endpointName}' declares an invalid or missing 'Port' ('{portText}').");
        }

        IPEndPoint bindEndPoint = new(ResolveHost(host), port);

        if (protocol is EndpointProtocol.Http1 or EndpointProtocol.Http2
            && ParseClientCertificateMode(endpoint, endpointName) is not ClientCertificateMode.NoCertificate)
        {
            throw new InvalidOperationException(
                $"The HTTP endpoint '{endpointName}' declares a 'ClientCertificateMode', but client certificates need TLS: use Protocol Https, Http1s, Http2s, or Http3.");
        }

        switch (protocol)
        {
            case EndpointProtocol.Http1:
                options.UseHttp1(
                    () => TcpConnectionListener.Create(tcp => tcp.EndPoint = bindEndPoint),
                    http1 => CopyHttp1Limits(limits.Http1, http1.Limits));
                break;
            case EndpointProtocol.Http2:
                options.UseHttp2(
                    () => TcpConnectionListener.Create(tcp => tcp.EndPoint = bindEndPoint),
                    http2 => CopyHttp2Limits(limits, http2.Limits));
                break;
            case EndpointProtocol.Https:
                options.UseHttps(
                    tcp => tcp.EndPoint = bindEndPoint,
                    CreateTlsOptions(endpoint, endpointName, ownCertificate, contentRootPath),
                    http1 => CopyHttp1Limits(limits.Http1, http1.Limits),
                    http2 => CopyHttp2Limits(limits, http2.Limits));
                break;
            case EndpointProtocol.Http1s:
                options.UseHttp1s(
                    tcp => tcp.EndPoint = bindEndPoint,
                    CreateTlsOptions(endpoint, endpointName, ownCertificate, contentRootPath),
                    http1 => CopyHttp1Limits(limits.Http1, http1.Limits));
                break;
            case EndpointProtocol.Http2s:
                options.UseHttp2s(
                    tcp => tcp.EndPoint = bindEndPoint,
                    CreateTlsOptions(endpoint, endpointName, ownCertificate, contentRootPath),
                    http2 => CopyHttp2Limits(limits, http2.Limits));
                break;
            case EndpointProtocol.Http3:
                // System.Net.Quic exists only on these operating systems; elsewhere an h3 endpoint can
                // never bind, so the configuration is refused here rather than at start.
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    BindHttp3Endpoint(endpoint, endpointName, bindEndPoint, options, limits, ownCertificate, contentRootPath);
                    break;
                }

                throw new PlatformNotSupportedException(
                    $"The HTTP endpoint '{endpointName}' declares 'Protocol' Http3, which requires QUIC (Windows, Linux, or macOS).");
        }

        return protocol;
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void BindHttp3Endpoint(
        IConfigurationSection endpoint,
        string endpointName,
        IPEndPoint bindEndPoint,
        HttpConnectionListenerOptions options,
        BoundLimits limits,
        Action<X509Certificate2>? ownCertificate,
        string? contentRootPath)
    {
        options.UseHttp3(
            quic => quic.EndPoint = bindEndPoint,
            CreateTlsOptions(endpoint, endpointName, ownCertificate, contentRootPath),
            http3 => CopySharedLimits(limits.Http1, http3.Limits));
    }

    private static TlsServerOptions CreateTlsOptions(
        IConfigurationSection endpoint,
        string endpointName,
        Action<X509Certificate2>? ownCertificate,
        string? contentRootPath)
    {
        (X509Certificate2 leaf, X509Certificate2Collection chain) = ResolveCertificate(endpoint, endpointName, contentRootPath);

        ownCertificate?.Invoke(leaf);
        foreach (X509Certificate2 issuer in chain)
        {
            ownCertificate?.Invoke(issuer);
        }

        TlsServerOptions tls = new()
        {
            AuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificateContext = SslStreamCertificateContext.Create(leaf, chain, offline: true),
            },
        };

        // Configuration cannot carry a validation callback, so a presented client certificate is
        // accepted only when it chains to a root the machine trusts (the platform's verdict).
        switch (ParseClientCertificateMode(endpoint, endpointName))
        {
            case ClientCertificateMode.AllowCertificate:
                tls.AllowClientCertificate();
                break;
            case ClientCertificateMode.RequireCertificate:
                tls.RequireClientCertificate();
                break;
        }

        return tls;
    }

    private static ClientCertificateMode ParseClientCertificateMode(IConfigurationSection endpoint, string endpointName)
    {
        string? mode = GetString(endpoint, "ClientCertificateMode");

        if (mode is null || mode.Equals("NoCertificate", StringComparison.OrdinalIgnoreCase))
        {
            return ClientCertificateMode.NoCertificate;
        }

        if (mode.Equals("AllowCertificate", StringComparison.OrdinalIgnoreCase))
        {
            return ClientCertificateMode.AllowCertificate;
        }

        if (mode.Equals("RequireCertificate", StringComparison.OrdinalIgnoreCase))
        {
            return ClientCertificateMode.RequireCertificate;
        }

        throw new InvalidOperationException(
            $"The HTTP endpoint '{endpointName}' declares an unsupported 'ClientCertificateMode' ('{mode}'). Supported values: NoCertificate, AllowCertificate, RequireCertificate.");
    }

    /// <summary>
    /// Resolves a TLS endpoint's certificate: from a file when <c>Certificate</c> is a section
    /// (<c>Path</c>, optional <c>KeyPath</c> and <c>Password</c>), otherwise from the Secret mount the
    /// scalar <c>Certificate</c> names (the endpoint's registered mount, or <c>tls</c>, when absent).
    /// </summary>
    private static (X509Certificate2 Leaf, X509Certificate2Collection Chain) ResolveCertificate(
        IConfigurationSection endpoint,
        string endpointName,
        string? contentRootPath)
    {
        if (endpoint.GetEntry("Certificate") is IConfigurationSection certificate)
        {
            string? path = GetString(certificate, "Path");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException(
                    $"The HTTPS endpoint '{endpointName}' declares a 'Certificate' section without a 'Path'.");
            }

            string? keyPath = GetString(certificate, "KeyPath");

            return LoadCertificateFile(
                endpointName,
                ResolvePath(path, contentRootPath),
                string.IsNullOrWhiteSpace(keyPath) ? null : ResolvePath(keyPath, contentRootPath),
                GetString(certificate, "Password"));
        }

        if (!ResourceRuntime.Current.TryGetEndpointCertificate(endpointName, GetString(endpoint, "Certificate"), out X509Certificate2? leaf, out X509Certificate2Collection chain))
        {
            throw new InvalidOperationException(
                $"The HTTPS endpoint '{endpointName}' requires its Certificate Secret mount (default 'tls') or a 'Certificate:Path' file.");
        }

        return (leaf, chain);
    }

    /// <summary>
    /// Loads a certificate and its private key from a PEM file (the key inline or in
    /// <paramref name="keyPath"/>) or a PKCS#12 file. A file is PKCS#12 when it opens with the DER
    /// <c>SEQUENCE</c> tag (<c>0x30</c>), which no PEM file does; a separate key file implies PEM.
    /// </summary>
    private static (X509Certificate2 Leaf, X509Certificate2Collection Chain) LoadCertificateFile(
        string endpointName,
        string path,
        string? keyPath,
        string? password)
    {
        X509Certificate2 leaf;
        X509Certificate2Collection chain;
        try
        {
            (leaf, chain) = keyPath is null && IsPkcs12(path)
                ? LoadPkcs12File(path, password)
                : LoadPemFile(path, keyPath, password);
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new InvalidOperationException(
                $"The HTTPS endpoint '{endpointName}' could not load its certificate from '{path}': {exception.Message}",
                exception);
        }

        DateTime now = DateTime.UtcNow;
        if (!leaf.HasPrivateKey || leaf.NotBefore.ToUniversalTime() > now || leaf.NotAfter.ToUniversalTime() <= now)
        {
            leaf.Dispose();
            foreach (X509Certificate2 issuer in chain)
            {
                issuer.Dispose();
            }

            throw new InvalidOperationException(
                $"The HTTPS endpoint '{endpointName}' certificate '{path}' requires a currently valid leaf and its private key.");
        }

        return (leaf, chain);
    }

    private static bool IsPkcs12(string path)
    {
        using System.IO.FileStream stream = System.IO.File.OpenRead(path);
        return stream.ReadByte() == 0x30;
    }

    private static (X509Certificate2 Leaf, X509Certificate2Collection Chain) LoadPkcs12File(string path, string? password)
    {
        X509Certificate2Collection certificates = X509CertificateLoader.LoadPkcs12CollectionFromFile(
            path,
            password,
            X509KeyStorageFlags.Exportable);

        X509Certificate2? leaf = null;
        X509Certificate2Collection chain = new();
        foreach (X509Certificate2 certificate in certificates)
        {
            if (leaf is null && certificate.HasPrivateKey)
            {
                leaf = certificate;
            }
            else
            {
                chain.Add(certificate);
            }
        }

        if (leaf is null)
        {
            foreach (X509Certificate2 certificate in chain)
            {
                certificate.Dispose();
            }

            throw new CryptographicException("The PKCS#12 file contains no certificate with a private key.");
        }

        return (leaf, chain);
    }

    private static (X509Certificate2 Leaf, X509Certificate2Collection Chain) LoadPemFile(string path, string? keyPath, string? password)
    {
        X509Certificate2 leaf;
        using (X509Certificate2 pem = password is null
            ? X509Certificate2.CreateFromPemFile(path, keyPath)
            : X509Certificate2.CreateFromEncryptedPemFile(path, password, keyPath))
        {
            // A PEM key is ephemeral; the PKCS#12 import gives SslStream a key association Windows
            // Schannel accepts for server authentication (the same import the Secret-mount loader does).
            byte[] pkcs12 = pem.Export(X509ContentType.Pkcs12);
            try
            {
                leaf = X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.Exportable);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }

        X509Certificate2Collection chain = new();
        X509Certificate2Collection bundle = new();
        try
        {
            bundle.ImportFromPemFile(path);
        }
        catch
        {
            leaf.Dispose();
            throw;
        }

        foreach (X509Certificate2 certificate in bundle)
        {
            if (certificate.RawDataMemory.Span.SequenceEqual(leaf.RawDataMemory.Span))
            {
                certificate.Dispose();
            }
            else
            {
                chain.Add(certificate);
            }
        }

        return (leaf, chain);
    }

    private static string ResolvePath(string path, string? contentRootPath)
    {
        return System.IO.Path.IsPathRooted(path)
            ? path
            : System.IO.Path.GetFullPath(path, contentRootPath ?? AppContext.BaseDirectory);
    }

    private static void CopyHttp1Limits(Http1ConnectionListenerOptions.Http1Limits source, Http1ConnectionListenerOptions.Http1Limits target)
    {
        target.MaxRequestLineSize = source.MaxRequestLineSize;
        target.MaxRequestHeaderCount = source.MaxRequestHeaderCount;
        target.MaxRequestHeadersTotalSize = source.MaxRequestHeadersTotalSize;
        CopySharedLimits(source, target);
    }

    private static void CopyHttp2Limits(BoundLimits source, Http2ConnectionListenerOptions.Http2Limits target)
    {
        CopySharedLimits(source.Http1, target);
        target.MaxStreamsPerConnection = source.Http2.MaxStreamsPerConnection;
        target.MaxRequestHeaderListSize = source.Http2.MaxRequestHeaderListSize;
        target.MaxResetStreamsPerWindow = source.Http2.MaxResetStreamsPerWindow;
        target.MaxSettingsFramesPerWindow = source.Http2.MaxSettingsFramesPerWindow;
        target.MaxPingFramesPerWindow = source.Http2.MaxPingFramesPerWindow;
        target.FloodDetectionWindow = source.Http2.FloodDetectionWindow;
    }

    private static void CopySharedLimits(HttpConnectionListenerLimits source, HttpConnectionListenerLimits target)
    {
        target.MaxRequestBodySize = source.MaxRequestBodySize;
        target.KeepAliveTimeout = source.KeepAliveTimeout;
        target.RequestHeadersTimeout = source.RequestHeadersTimeout;
    }

    private static EndpointProtocol ParseProtocol(string endpointName, string? protocol)
    {
        if (IsHttp1(protocol))
        {
            return EndpointProtocol.Http1;
        }

        if (IsHttp2(protocol))
        {
            return EndpointProtocol.Http2;
        }

        if (string.Equals(protocol, "Https", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointProtocol.Https;
        }

        if (string.Equals(protocol, "Http1s", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointProtocol.Http1s;
        }

        if (string.Equals(protocol, "Http2s", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointProtocol.Http2s;
        }

        if (IsHttp3(protocol))
        {
            return EndpointProtocol.Http3;
        }

        throw new InvalidOperationException(
            $"The HTTP endpoint '{endpointName}' declares an unsupported 'Protocol' ('{protocol}'). Supported values: Http1, Http2, Https, Http1s, Http2s, Http3.");
    }

    private static bool IsHttp1(string? protocol)
    {
        return protocol is null
            || protocol.Equals("Http1", StringComparison.OrdinalIgnoreCase)
            || protocol.Equals("Http/1.1", StringComparison.OrdinalIgnoreCase)
            || protocol.Equals("Http1.1", StringComparison.OrdinalIgnoreCase)
            || protocol.Equals("h1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHttp2(string? protocol)
    {
        return protocol is not null
            && (protocol.Equals("Http2", StringComparison.OrdinalIgnoreCase)
                || protocol.Equals("Http/2", StringComparison.OrdinalIgnoreCase)
                || protocol.Equals("Http2.0", StringComparison.OrdinalIgnoreCase)
                || protocol.Equals("h2", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHttp3(string? protocol)
    {
        return protocol is not null
            && (protocol.Equals("Http3", StringComparison.OrdinalIgnoreCase)
                || protocol.Equals("Http/3", StringComparison.OrdinalIgnoreCase)
                || protocol.Equals("Http3.0", StringComparison.OrdinalIgnoreCase)
                || protocol.Equals("h3", StringComparison.OrdinalIgnoreCase));
    }

    private static IPAddress ResolveHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        if (host is "*" or "+" or "0.0.0.0")
        {
            return IPAddress.Any;
        }

        if (host is "[::]" or "::")
        {
            return IPAddress.IPv6Any;
        }

        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return address;
        }

        throw new InvalidOperationException(
            $"The HTTP endpoint 'Host' value '{host}' is not a literal IP address, 'localhost', or a wildcard. DNS resolution is not performed at bind time.");
    }

    private static long? ParseMaxRequestBodySize(string value)
    {
        if (value.Equals("unbounded", StringComparison.OrdinalIgnoreCase)
            || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"The configured MaxRequestBodySize value '{value}' is not a non-negative integer or 'unbounded'.");
    }

    private static bool TryGetInt(IConfiguration configuration, string path, out int value)
    {
        string? raw = GetString(configuration, path);
        if (raw is null)
        {
            value = 0;
            return false;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            throw new InvalidOperationException($"The configured value at '{path}' ('{raw}') is not a valid integer.");
        }

        return true;
    }

    private static bool TryGetTimeout(IConfiguration configuration, string path, out TimeSpan value)
    {
        string? raw = GetString(configuration, path);
        if (raw is null)
        {
            value = default;
            return false;
        }

        if (raw.Equals("infinite", StringComparison.OrdinalIgnoreCase) || raw == "-1")
        {
            value = System.Threading.Timeout.InfiniteTimeSpan;
            return true;
        }

        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
        {
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        throw new InvalidOperationException(
            $"The configured timeout at '{path}' ('{raw}') is not a valid TimeSpan, a whole number of seconds, or 'infinite'.");
    }

    private static string? GetString(IConfiguration configuration, Path path)
    {
        return configuration.GetValue(path)?.Value;
    }

    private static string? GetString(IConfigurationSection section, Path relativePath)
    {
        return section.GetEntry(relativePath) is IConfigurationValue value ? value.Value : null;
    }

    /// <summary>
    /// A TLS endpoint's <c>ClientCertificateMode</c> (Kestrel's names).
    /// </summary>
    private enum ClientCertificateMode
    {
        NoCertificate,
        AllowCertificate,
        RequireCertificate,
    }

    /// <summary>
    /// The registration verb an endpoint's <c>Protocol</c> binds to.
    /// </summary>
    private enum EndpointProtocol
    {
        Http1,
        Http2,
        Https,
        Http1s,
        Http2s,
        Http3,
    }

    /// <summary>
    /// The parsed <c>Limits</c> section: the HTTP/1.1 template carries the shared keys too, and the
    /// HTTP/2 template carries only the <c>Limits:Http2</c> keys.
    /// </summary>
    private sealed class BoundLimits
    {
        public Http1ConnectionListenerOptions.Http1Limits Http1 { get; } = new();

        public Http2ConnectionListenerOptions.Http2Limits Http2 { get; } = new();
    }
}

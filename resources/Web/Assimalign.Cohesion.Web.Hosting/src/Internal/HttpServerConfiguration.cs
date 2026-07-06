using System;
using System.Globalization;
using System.Net;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http.Connections;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// Binds HTTP server listener endpoints and <see cref="HttpServerLimits"/> from a Cohesion
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
///     "Primary":  { "Protocol": "Http1", "Host": "localhost", "Port": 8080 },
///     "Insecure": { "Protocol": "Http2", "Host": "0.0.0.0",   "Port": 8081 }
///   },
///   "Limits": {
///     "MaxRequestLineSize":        8192,
///     "MaxRequestHeaderCount":     100,
///     "MaxRequestHeadersTotalSize": 32768,
///     "MaxRequestBodySize":        30000000,
///     "KeepAliveTimeout":          "00:02:10",
///     "RequestHeadersTimeout":     "00:00:30"
///   }
/// }
/// </code>
/// </remarks>
internal static class HttpServerConfiguration
{
    /// <summary>
    /// The default configuration section key the server binds from when the caller does not
    /// specify one.
    /// </summary>
    public const string DefaultSectionKey = "Http";

    /// <summary>
    /// Binds the endpoints and limits declared under <paramref name="sectionKey"/> onto
    /// <paramref name="options"/>.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <param name="sectionKey">The root section key (for example <c>"Http"</c>).</param>
    /// <param name="options">The listener options to populate.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a configured value cannot be parsed.</exception>
    public static void Bind(IConfiguration configuration, string sectionKey, HttpConnectionListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(sectionKey);
        ArgumentNullException.ThrowIfNull(options);

        BindLimits(configuration, sectionKey, options.Limits);
        BindEndpoints(configuration, sectionKey, options);
    }

    private static void BindLimits(IConfiguration configuration, string sectionKey, HttpServerLimits limits)
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

    private static void BindEndpoints(IConfiguration configuration, string sectionKey, HttpConnectionListenerOptions options)
    {
        IConfigurationSection? endpoints = configuration.GetSection($"{sectionKey}:Endpoints");
        if (endpoints is null)
        {
            return;
        }

        foreach (IConfigurationEntry child in endpoints.GetChildren())
        {
            if (child is IConfigurationSection endpoint)
            {
                BindEndpoint(endpoint, options);
            }
        }
    }

    private static void BindEndpoint(IConfigurationSection endpoint, HttpConnectionListenerOptions options)
    {
        string endpointName = endpoint.Key.ToString();
        string? protocol = GetString(endpoint, "Protocol");
        string? host = GetString(endpoint, "Host");
        string? portText = GetString(endpoint, "Port");

        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port is < 0 or > 65535)
        {
            throw new InvalidOperationException(
                $"The HTTP endpoint '{endpointName}' declares an invalid or missing 'Port' ('{portText}').");
        }

        IPEndPoint bindEndPoint = new(ResolveHost(host), port);

        if (IsHttp2(protocol))
        {
            options.UseHttp2(tcp => tcp.EndPoint = bindEndPoint);
        }
        else if (IsHttp1(protocol))
        {
            options.UseHttp1(tcp => tcp.EndPoint = bindEndPoint);
        }
        else
        {
            throw new InvalidOperationException(
                $"The HTTP endpoint '{endpointName}' declares an unsupported 'Protocol' ('{protocol}'). Supported values: Http1, Http2.");
        }
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
}

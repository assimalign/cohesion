using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.OpenTelemetry;

namespace Assimalign.Cohesion.Hosting.Telemetry;

/// <summary>Composes opt-in log export from one resource's ambient environment.</summary>
public static class ResourceTelemetry
{
    /// <summary>Tests the gateway and endpoint gate without constructing a logger factory.</summary>
    /// <param name="context">The invocation context.</param>
    /// <returns>Whether an absolute HTTP(S) collector is configured for a gateway-owned resource.</returns>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    /// <exception cref="InvalidOperationException">A supplied endpoint is malformed.</exception>
    public static bool IsEnabled(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GatewayName is null || !context.TryGetEnvironmentValue(ResourceEnvironment.TelemetryEndpoint, out string? value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new InvalidOperationException($"{nameof(ResourceEnvironment.TelemetryEndpoint)} requires an absolute HTTP(S) collector endpoint.");
        }

        return true;
    }

    /// <summary>Adds providers to an existing builder; the caller must dispose its factory.</summary>
    /// <param name="context">The invocation context.</param>
    /// <param name="logging">The unbuilt logging composition surface.</param>
    /// <returns>False without mutation when telemetry is disabled.</returns>
    /// <exception cref="InvalidOperationException">Configuration or builder state is invalid.</exception>
    /// <exception cref="ArgumentNullException">The context, or an enabled logging builder, is null.</exception>
    /// <exception cref="IOException">The configured headers file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The configured headers file is inaccessible.</exception>
    /// <exception cref="CryptographicException">The protected headers file cannot be unwrapped.</exception>
    /// <exception cref="FormatException">A configured HTTP header is invalid.</exception>
    /// <exception cref="NotSupportedException"><see cref="ResourceEnvironment.TelemetryProtocol"/> selects the reserved gRPC protocol.</exception>
    public static bool Configure(ResourceContext context, ILoggerFactoryBuilder logging)
        => Configure(context, logging, out _);

    /// <summary>Creates a factory only when telemetry is configured; the caller owns disposal.</summary>
    /// <param name="context">The invocation context.</param>
    /// <returns>An owned logger factory, or null without allocating a builder when disabled.</returns>
    /// <exception cref="InvalidOperationException">Configuration is invalid.</exception>
    /// <exception cref="ArgumentNullException">The context, or an enabled logging builder, is null.</exception>
    /// <exception cref="IOException">The configured headers file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The configured headers file is inaccessible.</exception>
    /// <exception cref="CryptographicException">The protected headers file cannot be unwrapped.</exception>
    /// <exception cref="FormatException">A configured HTTP header is invalid.</exception>
    /// <exception cref="NotSupportedException"><see cref="ResourceEnvironment.TelemetryProtocol"/> selects the reserved gRPC protocol.</exception>
    public static ILoggerFactory? Configure(ResourceContext context) => Configure(context, out _);

    /// <summary>Configures an existing builder and returns the service that must be registered for bounded host-stop flush.</summary>
    /// <param name="context">The invocation context.</param>
    /// <param name="logging">The existing unbuilt factory builder; it is never built by this method.</param>
    /// <param name="lifetime">The stop service, or null when disabled. Register before producers so it stops last.</param>
    /// <returns>Whether telemetry providers were added.</returns>
    /// <exception cref="InvalidOperationException">Configuration or builder state is invalid.</exception>
    /// <exception cref="ArgumentNullException">The context, or an enabled logging builder, is null.</exception>
    /// <exception cref="IOException">The configured headers file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The configured headers file is inaccessible.</exception>
    /// <exception cref="CryptographicException">The protected headers file cannot be unwrapped.</exception>
    /// <exception cref="FormatException">A configured HTTP header is invalid.</exception>
    /// <exception cref="NotSupportedException"><see cref="ResourceEnvironment.TelemetryProtocol"/> selects the reserved gRPC protocol.</exception>
    public static bool Configure(ResourceContext context, ILoggerFactoryBuilder logging, out IHostService? lifetime)
    {
        lifetime = null;
        if (!IsEnabled(context))
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(logging);
        OtlpExporterOptions options = CreateOptions(context);
        var provider = new OtlpLoggerProvider(OtlpExporter.CreateLogExporter(options));
        try { logging.AddProvider(provider); }
        catch { provider.Dispose(); throw; }
        if (context.TryGetEnvironmentValue(ResourceEnvironment.LogFormat, out string? format) &&
            string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            var console = new ConsoleLoggerProvider(new ConsoleLoggerOptions { Formatter = JsonConsoleFormatter.Write });
            try { logging.AddProvider(console); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("already registered", StringComparison.Ordinal))
            {
                // An explicitly registered Console provider wins; the builder exposes no provider enumeration.
                console.Dispose();
            }
        }
        lifetime = new TelemetryHostService(provider);
        return true;
    }

    /// <summary>Creates the optional logger factory and its mandatory host-stop service.</summary>
    /// <param name="context">The invocation context.</param>
    /// <param name="lifetime">Register this service before producers; null when disabled.</param>
    /// <returns>The owned factory, or null when disabled.</returns>
    /// <exception cref="InvalidOperationException">Configuration is invalid.</exception>
    /// <exception cref="ArgumentNullException">The context, or an enabled logging builder, is null.</exception>
    /// <exception cref="IOException">The configured headers file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The configured headers file is inaccessible.</exception>
    /// <exception cref="CryptographicException">The protected headers file cannot be unwrapped.</exception>
    /// <exception cref="FormatException">A configured HTTP header is invalid.</exception>
    /// <exception cref="NotSupportedException"><see cref="ResourceEnvironment.TelemetryProtocol"/> selects the reserved gRPC protocol.</exception>
    public static ILoggerFactory? Configure(ResourceContext context, out IHostService? lifetime)
    {
        lifetime = null;
        if (!IsEnabled(context))
        {
            return null;
        }

        var logging = new LoggerFactoryBuilder();
        Configure(context, logging, out lifetime);
        ILoggerFactory factory = logging.Build();
        ((TelemetryHostService)lifetime!).OwnedFactory = factory;
        return factory;
    }

    internal static OtlpExporterOptions CreateOptions(ResourceContext context)
    {
        context.TryGetEnvironmentValue(ResourceEnvironment.TelemetryProtocol, out string? protocol);
        if (string.Equals(protocol, "otlp-grpc", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"{nameof(ResourceEnvironment.TelemetryProtocol)}: otlp-grpc is reserved; this build serves OTLP over HTTP only.");
        }

        if (!string.IsNullOrWhiteSpace(protocol) && !string.Equals(protocol, "otlp-http", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{nameof(ResourceEnvironment.TelemetryProtocol)} accepts otlp-http or reserved otlp-grpc.");
        }

        context.TryGetEnvironmentValue(ResourceEnvironment.TelemetryEndpoint, out string? endpoint);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (context.TryGetEnvironmentValue(ResourceEnvironment.TelemetryHeadersPath, out string? path))
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException($"{nameof(ResourceEnvironment.TelemetryHeadersPath)} requires an absolute file path.");
            }
            // An actually empty document contains no protected payload to unwrap on Windows.
            byte[] bytes = new FileInfo(path).Length == 0 ? [] : new ResourceMount(path).ReadAllBytes();
            try { headers = ParseHeaders(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var validator = context.CreateOutboundTrustValidator();
        return new OtlpExporterOptions
        {
            Endpoint = new Uri(endpoint!, UriKind.Absolute), Headers = headers, Timeout = TimeSpan.FromSeconds(5),
            ResourceAttributes = new Dictionary<string, string>
            {
                ["service.name"] = context.ResourceName ?? string.Empty,
                ["service.namespace"] = context.ApplicationName ?? string.Empty,
                ["deployment.environment.name"] = context.EnvironmentName
            },
            HandlerFactory = () =>
            {
                var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
                handler.SslOptions.RemoteCertificateValidationCallback = validator;
                return handler;
            }
        };
    }

    internal static Dictionary<string, string> ParseHeaders(ReadOnlySpan<byte> bytes)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new InvalidOperationException($"{nameof(ResourceEnvironment.TelemetryHeadersPath)} contains a malformed header line.");
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (value.Contains('\r') || value.Contains('\n'))
            {
                throw new InvalidOperationException($"{nameof(ResourceEnvironment.TelemetryHeadersPath)} contains a malformed header value.");
            }

            headers[name] = value; // Last occurrence wins, case-insensitively.
        }
        return headers;
    }
}

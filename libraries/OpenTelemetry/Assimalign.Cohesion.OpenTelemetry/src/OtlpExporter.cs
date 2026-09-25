using System;

using Assimalign.Cohesion.OpenTelemetry.Internal;

namespace Assimalign.Cohesion.OpenTelemetry;

/// <summary>Constructs transport-only OTLP exporters.</summary>
public static class OtlpExporter
{
    /// <summary>Creates a bounded OTLP/HTTP JSON log exporter and starts its drain loop.</summary>
    /// <param name="options">Transport, queue, and resource settings; copied at construction.</param>
    /// <returns>An owned exporter.</returns>
    /// <exception cref="ArgumentNullException">Options or its endpoint are null.</exception>
    /// <exception cref="ArgumentException">An endpoint, header, or queue setting is invalid.</exception>
    /// <exception cref="NotSupportedException">The protocol is unsupported.</exception>
    public static IOtlpLogExporter CreateLogExporter(OtlpExporterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("http" or "https") ||
            options.Endpoint.UserInfo.Length != 0 || options.Endpoint.Query.Length != 0 || options.Endpoint.Fragment.Length != 0)
        {
            throw new ArgumentException("The collector must be an absolute HTTP(S) base endpoint.", nameof(options));
        }

        if (options.Protocol != OtlpProtocol.HttpJson)
        {
            throw new NotSupportedException("Only OTLP/HTTP JSON log export is implemented.");
        }

        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(5) ||
            options.FlushInterval <= TimeSpan.Zero || options.FlushInterval > TimeSpan.FromDays(1) ||
            options.MaxQueueLength <= 0 || options.MaxBatchSize <= 0)
        {
            throw new ArgumentException("Timeout, interval, queue length, and batch size must be positive and bounded.", nameof(options));
        }

        return new HttpJsonLogExporter(options);
    }
}

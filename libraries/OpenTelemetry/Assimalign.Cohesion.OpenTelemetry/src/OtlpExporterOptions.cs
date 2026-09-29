using System;
using System.Collections.Generic;
using System.Net.Http;

namespace Assimalign.Cohesion.OpenTelemetry;

/// <summary>Configures one process's bounded OTLP log exporter.</summary>
public sealed class OtlpExporterOptions
{
    /// <summary>Gets or sets the absolute HTTP(S) collector base endpoint.</summary>
    public Uri Endpoint { get; set; } = null!;
    /// <summary>Gets or sets the wire encoding.</summary>
    public OtlpProtocol Protocol { get; set; } = OtlpProtocol.HttpJson;
    /// <summary>Gets or sets request headers. Values must not contain line breaks.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    /// <summary>Gets or sets attributes identifying the emitting resource.</summary>
    public IReadOnlyDictionary<string, string> ResourceAttributes { get; set; } = new Dictionary<string, string>();
    /// <summary>Gets or sets the total export and disposal budget, including retries; defaults to ten seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Gets or sets queue capacity. Overflow drops the oldest record; defaults to 2048.</summary>
    public int MaxQueueLength { get; set; } = 2048;
    /// <summary>Gets or sets the background flush interval; defaults to five seconds.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Gets or sets the maximum records in one request; defaults to 512.</summary>
    public int MaxBatchSize { get; set; } = 512;
    /// <summary>Gets or sets a factory for an owned handler with caller-defined transport trust.</summary>
    public Func<HttpMessageHandler>? HandlerFactory { get; set; }
}

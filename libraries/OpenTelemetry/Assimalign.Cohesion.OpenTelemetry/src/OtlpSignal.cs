namespace Assimalign.Cohesion.OpenTelemetry;

/// <summary>Reserved OTLP signal identities. Only log export is implemented.</summary>
public enum OtlpSignal
{
    /// <summary>Structured log records.</summary>
    Logs,
    /// <summary>Distributed traces; reserved for future implementation.</summary>
    Traces,
    /// <summary>Instrument measurements; reserved for future implementation.</summary>
    Metrics
}

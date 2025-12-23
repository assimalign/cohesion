namespace System.Diagnostics.Telemetry;

public interface ITelemetryArgs<T> where T : struct, ITelemetryArgs<T>
{
    /// <summary>
    /// 
    /// </summary>
    TelemetryKind Kind { get; }

    /// <summary>
    /// 
    /// </summary>
    TelemetryEvent Event { get; }

    /// <summary>
    /// 
    /// </summary>
    TelemetrySource? Source { get; }
}
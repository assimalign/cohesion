namespace System.Diagnostics.Telemetry;

public interface ITelemetryArgs<T, TArguments> : ITelemetryArgs<T> where T : struct, ITelemetryArgs<T>
{
    /// <summary>
    /// 
    /// </summary>
    TArguments? Arguments { get; }
}

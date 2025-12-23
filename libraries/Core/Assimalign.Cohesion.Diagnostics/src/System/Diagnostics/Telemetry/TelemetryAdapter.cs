namespace System.Diagnostics.Telemetry;

public abstract class TelemetryAdapter
{
    /// <summary>
    /// Write a telemetry item. Uses constrained calls to avoid boxing.
    /// </summary>
    public abstract void Write<T>(in T args) where T : struct, ITelemetryArgs<T>;

    /// <summary>
    /// Write a telemetry item with strongly-typed arguments. Uses constrained calls to avoid boxing.
    /// </summary>
    public abstract void Write<T, TArguments>(in T args) where T : struct, ITelemetryArgs<T, TArguments>;
}


public abstract class TelemetryAdaptor<T> : TelemetryAdapter where T : struct, ITelemetryArgs<T>
{
    public abstract void Write(in T args);
    public sealed override void Write<T1, TArguments>(in T1 args)
    {
        if (args is ITelemetryArgs<T, TArguments> and T typedArgs)
        {
            Write(typedArgs);
        }
    }
    public sealed override void Write<T1>(in T1 args)
    {
        if (args is T typedArgs)
        {
            Write(typedArgs);
        }
    }
}
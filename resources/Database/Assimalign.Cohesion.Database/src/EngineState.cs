namespace Assimalign.Cohesion.Database;

/// <summary>
/// Represents the lifecycle state of a database engine.
/// </summary>
public enum EngineState : byte
{
    /// <summary>The engine has not been started.</summary>
    Idle = 0,
    /// <summary>The engine is initializing subsystems.</summary>
    Starting,
    /// <summary>The engine is running and accepting operations.</summary>
    Running,
    /// <summary>The engine is shutting down.</summary>
    Stopping,
    /// <summary>The engine has been stopped.</summary>
    Stopped,
    /// <summary>The engine encountered an unrecoverable error.</summary>
    Faulted
}

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The observational state of a database engine. An engine is a data machine —
/// operational from creation, terminal on disposal — so this enum reports
/// condition, not lifecycle phases.
/// </summary>
public enum EngineState : byte
{
    /// <summary>
    /// The engine is operational: databases can be created, opened, and used.
    /// This is the state from creation.
    /// </summary>
    Running = 0,

    /// <summary>
    /// A background worker keeps failing. The worker keeps running and retries after
    /// a backoff, the engine keeps serving and its correctness guarantees hold
    /// (grouped commits self-help within their window, a checkpoint that cannot run
    /// leaves the journal untruncated), but it runs degraded and the owner should
    /// learn why (<see cref="DatabaseEngineWorker.Fault"/>). The state returns to
    /// <see cref="Running"/> once the failing worker finishes the work its failures
    /// left (#1268).
    /// </summary>
    Faulted,

    /// <summary>
    /// The engine has been disposed: workers quiesced, open databases durably
    /// flushed and closed. Terminal.
    /// </summary>
    Disposed,
}

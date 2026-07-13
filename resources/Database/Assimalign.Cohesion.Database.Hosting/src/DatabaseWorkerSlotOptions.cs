namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>
/// Maps one kind of engine-owned worker onto the host's execution menu: whether the
/// host claims workers of the kind at all, and which execution model drives them.
/// </summary>
/// <remarks>
/// Disabling a slot never loses the work — an unclaimed worker is self-scheduled by
/// its engine when the engine starts (requirement R10: the engine owns the work; the
/// host only chooses the scheduler). Cadence is not configured here: it lives on the
/// engine's own options (the engine owns the work loop whether or not a host maps
/// it), and the host reads it through <see cref="IDatabaseEngineWorker.Interval"/>.
/// </remarks>
public sealed class DatabaseWorkerSlotOptions
{
    internal DatabaseWorkerSlotOptions(DatabaseWorkerExecution execution)
    {
        Execution = execution;
    }

    /// <summary>
    /// Gets or sets whether the host claims workers of this kind and drives them as
    /// host services. Defaults to <see langword="true"/>. When
    /// <see langword="false"/> the engine self-schedules the worker itself.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the execution-menu member claimed workers of this kind run on.
    /// Defaults follow the execution-model mapping in docs/DESIGN.md.
    /// </summary>
    public DatabaseWorkerExecution Execution { get; set; }
}

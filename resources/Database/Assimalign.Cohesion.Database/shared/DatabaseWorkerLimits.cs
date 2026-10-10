using System;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

/// <summary>
/// The one copy of the worker limits every model engine checks and resolves from its options (owner
/// decisions 25 of 2026-10-06 and 42 of 2026-10-07; database-area.md rule 8): how long, and across
/// how many failed passes in a row, one database's worker failures may persist before its engine
/// takes it offline, and how long a database's journal may grow while its checkpoints keep failing.
/// </summary>
/// <remarks>
/// <para>
/// Compiled into each model assembly from the root's <c>shared</c> folder, as the engines'
/// checkpointer is: each model has its own options type, and the root's public surface has no
/// options type to put the checks on. The failure window and minimum themselves are enforced by
/// the root engine base (<see cref="DatabaseEngine.WorkerFailureWindow"/>,
/// <see cref="DatabaseEngine.WorkerFailureMinimumPasses"/>), which refuses the same values; the
/// check here runs first, before the engine's worker threads start, and names the option.
/// </para>
/// <para>
/// <b>The journal size limit.</b> Zero, the options' default, resolves to four times the engine's
/// checkpoint journal size: 1 GiB at its 256 MiB default, PostgreSQL's <c>max_wal_size</c> default
/// (<c>src/backend/access/transam/xlog.c:121</c>), the WAL volume PostgreSQL lets build up between
/// checkpoints. With the size trigger off (a checkpoint journal size of zero) it resolves to 1 GiB.
/// A limit set explicitly must be at least the checkpoint journal size, or a journal would pass it
/// before it is ever due for a checkpoint.
/// </para>
/// </remarks>
internal static class DatabaseWorkerLimits
{
    /// <summary>
    /// The multiple of the checkpoint journal size a database's journal may reach while its
    /// checkpoints keep failing, when the options set no journal size limit.
    /// </summary>
    internal const long JournalSizeLimitFactor = 4;

    /// <summary>
    /// The journal size limit when the options set none and the size trigger is off: 1 GiB.
    /// </summary>
    internal const long DefaultJournalSizeLimit = 1024L * 1024 * 1024;

    /// <summary>
    /// Checks an engine's worker limits before the engine is created.
    /// </summary>
    /// <param name="workerFailureWindow">The options' worker failure window.</param>
    /// <param name="workerFailureMinimumPasses">The options' minimum of failed passes.</param>
    /// <param name="journalSizeLimit">The options' journal size limit; zero for the default.</param>
    /// <param name="checkpointJournalSize">The options' checkpoint journal size, already checked.</param>
    /// <param name="workerFailureWindowName">The worker failure window option's name, for the exception.</param>
    /// <param name="workerFailureMinimumPassesName">The minimum of failed passes option's name, for the exception.</param>
    /// <param name="journalSizeLimitName">The journal size limit option's name, for the exception.</param>
    /// <param name="engine">
    /// The engine, as <see cref="DatabaseEngineOptionChecks.Describe"/> names it, which each refusal's
    /// message starts with.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The worker failure window is not positive or is longer than
    /// <see cref="DatabaseEngine.MaximumWorkerFailureWindow"/>; the minimum of failed passes is less
    /// than one; or the journal size limit is negative, or set and smaller than the checkpoint
    /// journal size.
    /// </exception>
    internal static void Validate(TimeSpan workerFailureWindow, int workerFailureMinimumPasses, long journalSizeLimit, long checkpointJournalSize,
        string workerFailureWindowName, string workerFailureMinimumPassesName, string journalSizeLimitName, string engine)
    {
        DatabaseEngineOptionChecks.ThrowIfNotPositive(workerFailureWindow, engine, workerFailureWindowName);
        if (workerFailureWindow > DatabaseEngine.MaximumWorkerFailureWindow)
        {
            throw DatabaseEngineOptionChecks.Refuse(engine, workerFailureWindowName, workerFailureWindow,
                $"must be at most {DatabaseEngine.MaximumWorkerFailureWindow}.");
        }

        DatabaseEngineOptionChecks.ThrowIfNotPositive(workerFailureMinimumPasses, engine, workerFailureMinimumPassesName);
        DatabaseEngineOptionChecks.ThrowIfNegative(journalSizeLimit, engine, journalSizeLimitName);
        if (journalSizeLimit > 0 && journalSizeLimit < checkpointJournalSize)
        {
            throw DatabaseEngineOptionChecks.Refuse(engine, journalSizeLimitName, journalSizeLimit,
                $"must be zero (the default) or at least the checkpoint journal size ({checkpointJournalSize} bytes): " +
                "a journal would otherwise pass it before it is due for a checkpoint.");
        }
    }

    /// <summary>
    /// Resolves the journal size limit an engine's checkpointer applies.
    /// </summary>
    /// <param name="journalSizeLimit">The options' journal size limit; zero for the default.</param>
    /// <param name="checkpointJournalSize">The options' checkpoint journal size.</param>
    /// <returns>The limit, in bytes.</returns>
    internal static long GetJournalSizeLimit(long journalSizeLimit, long checkpointJournalSize)
    {
        if (journalSizeLimit > 0)
        {
            return journalSizeLimit;
        }

        if (checkpointJournalSize <= 0)
        {
            return DefaultJournalSizeLimit;
        }

        return checkpointJournalSize > long.MaxValue / JournalSizeLimitFactor
            ? long.MaxValue
            : checkpointJournalSize * JournalSizeLimitFactor;
    }
}

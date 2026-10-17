using System;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

/// <summary>
/// The option checks every model engine makes before it is created, worded once: each refusal
/// names the engine and the option, so a host that builds several engines says which one was
/// misconfigured (B3 of the engine extensibility design).
/// </summary>
/// <remarks>
/// Compiled into each model assembly from the root's <c>shared</c> folder, as
/// <see cref="DatabaseWorkerLimits"/> is: each model has its own options type, and the root's public
/// surface has no options type to put the checks on. Every refusal is an
/// <see cref="ArgumentOutOfRangeException"/> whose <see cref="ArgumentException.ParamName"/> is the
/// option's name and whose <see cref="ArgumentOutOfRangeException.ActualValue"/> is the value refused;
/// its message starts with the engine, as in <c>Graph engine 'orders': CheckpointInterval must be
/// positive.</c>
/// </remarks>
internal static class DatabaseEngineOptionChecks
{
    /// <summary>
    /// Names an engine the way every option refusal starts: <c>{model} engine '{name}'</c>.
    /// </summary>
    /// <param name="model">The model's name as its messages start (<c>SQL</c>, <c>Key-value</c>, …).</param>
    /// <param name="engineName">The engine name.</param>
    /// <returns>The engine, described.</returns>
    internal static string Describe(string model, string engineName) => $"{model} engine '{engineName}'";

    /// <summary>
    /// Creates a refusal of one option.
    /// </summary>
    /// <param name="engine">The engine, as <see cref="Describe"/> names it.</param>
    /// <param name="option">The option's name.</param>
    /// <param name="value">The value refused.</param>
    /// <param name="requirement">What the option must be, completing "<c>{option} …</c>".</param>
    /// <returns>The exception to throw.</returns>
    internal static ArgumentOutOfRangeException Refuse(string engine, string option, object? value, string requirement)
        => new(option, value, $"{engine}: {option} {requirement}");

    /// <summary>Refuses a cadence or timeout that is zero or negative.</summary>
    /// <param name="value">The value.</param>
    /// <param name="engine">The engine, as <see cref="Describe"/> names it.</param>
    /// <param name="option">The option's name.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not positive.</exception>
    internal static void ThrowIfNotPositive(TimeSpan value, string engine, string option)
    {
        if (value <= TimeSpan.Zero)
        {
            throw Refuse(engine, option, value, "must be positive.");
        }
    }

    /// <summary>Refuses a count that is zero or negative.</summary>
    /// <param name="value">The value.</param>
    /// <param name="engine">The engine, as <see cref="Describe"/> names it.</param>
    /// <param name="option">The option's name.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not positive.</exception>
    internal static void ThrowIfNotPositive(int value, string engine, string option)
    {
        if (value <= 0)
        {
            throw Refuse(engine, option, value, "must be positive.");
        }
    }

    /// <summary>Refuses a size that is negative.</summary>
    /// <param name="value">The value.</param>
    /// <param name="engine">The engine, as <see cref="Describe"/> names it.</param>
    /// <param name="option">The option's name.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    internal static void ThrowIfNegative(long value, string engine, string option)
    {
        if (value < 0)
        {
            throw Refuse(engine, option, value, "must not be negative.");
        }
    }

    /// <summary>
    /// Refuses a grouped-commit window that is not positive, or longer than a monitor wait takes
    /// (<see cref="Assimalign.Cohesion.Database.Storage.Storage.MaximumGroupCommitWindow"/>): the
    /// window is also the flush worker's wake cadence. Checked before any file is touched, rather
    /// than by the storage setter at database create or open (owner decision 26 of 2026-10-06).
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="engine">The engine, as <see cref="Describe"/> names it.</param>
    /// <param name="option">The option's name.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is outside the range.</exception>
    internal static void ThrowIfInvalidGroupCommitWindow(TimeSpan value, string engine, string option)
    {
        ThrowIfNotPositive(value, engine, option);
        if (value > Assimalign.Cohesion.Database.Storage.Storage.MaximumGroupCommitWindow)
        {
            throw Refuse(engine, option, value,
                $"must be at most {Assimalign.Cohesion.Database.Storage.Storage.MaximumGroupCommitWindow}.");
        }
    }

    /// <summary>
    /// Converts a buffer pool capacity in bytes to pages after checking it, as
    /// <see cref="Assimalign.Cohesion.Database.Storage.Storage.GetBufferPoolPageCount"/> does, with
    /// the refusal naming the engine.
    /// </summary>
    /// <param name="capacity">The capacity in bytes.</param>
    /// <param name="engine">The engine, as <see cref="Describe"/> names it.</param>
    /// <param name="option">The option's name.</param>
    /// <returns>The capacity in pages.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is not a valid pool size.</exception>
    internal static int GetBufferPoolPageCount(long capacity, string engine, string option)
    {
        try
        {
            return Assimalign.Cohesion.Database.Storage.Storage.GetBufferPoolPageCount(capacity, option);
        }
        catch (ArgumentOutOfRangeException)
        {
            const long page = Assimalign.Cohesion.Database.Storage.Units.Page.Size;
            const long minimum = Assimalign.Cohesion.Database.Storage.Storage.MinimumBufferPoolBytes;
            throw Refuse(engine, option, capacity,
                $"must be a whole number of {page}-byte pages, at least {minimum} bytes ({minimum / page} pages) " +
                $"and at most {(long)int.MaxValue * page} bytes.");
        }
    }
}

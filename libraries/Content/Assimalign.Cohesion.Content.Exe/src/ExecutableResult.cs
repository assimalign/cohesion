using System;

namespace Assimalign.Cohesion.Content.Exe;

/// <summary>A completed invocation with bounded UTF-8 output and no owned process handles.</summary>
public sealed class ExecutableResult
{
    private ExecutableResult() { }

    /// <summary>Gets the OS exit code, or null when no process started.</summary>
    public int? ExitCode { get; private init; }
    /// <summary>Gets captured stdout, decoded as UTF-8 with replacement for incomplete sequences.</summary>
    public string StandardOutput { get; private init; } = string.Empty;
    /// <summary>Gets captured stderr, decoded as UTF-8 with replacement for incomplete sequences.</summary>
    public string StandardError { get; private init; } = string.Empty;
    /// <summary>Gets whether stdout exceeded its byte cap.</summary>
    public bool StandardOutputTruncated { get; private init; }
    /// <summary>Gets whether stderr exceeded its byte cap.</summary>
    public bool StandardErrorTruncated { get; private init; }
    /// <summary>Gets the UTC invocation-attempt time.</summary>
    public DateTimeOffset StartedAt { get; private init; }
    /// <summary>Gets UTC end time, projected from start using monotonic elapsed time.</summary>
    public DateTimeOffset EndedAt { get; private init; }
    /// <summary>Gets duration including cleanup.</summary>
    public TimeSpan Duration => EndedAt - StartedAt;
    /// <summary>Gets the completion reason.</summary>
    public ExecutableTerminationReason TerminationReason { get; private init; }

    /// <summary>Creates a result, including for custom platform invokers.</summary>
    /// <param name="exitCode">Exit code, or null if no process started.</param>
    /// <param name="standardOutput">Captured stdout.</param>
    /// <param name="standardError">Captured stderr.</param>
    /// <param name="standardOutputTruncated">Whether stdout exceeded its cap.</param>
    /// <param name="standardErrorTruncated">Whether stderr exceeded its cap.</param>
    /// <param name="startedAt">Invocation attempt time.</param>
    /// <param name="endedAt">End time, no earlier than start.</param>
    /// <param name="terminationReason">Completion reason.</param>
    /// <returns>An immutable completed result.</returns>
    /// <exception cref="ArgumentNullException">An output string is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Times are reversed or the reason is undefined.</exception>
    /// <exception cref="ArgumentException">An exited result has no exit code, or a failed start has one.</exception>
    public static ExecutableResult Create(
        int? exitCode, string standardOutput, string standardError,
        bool standardOutputTruncated, bool standardErrorTruncated,
        DateTimeOffset startedAt, DateTimeOffset endedAt, ExecutableTerminationReason terminationReason)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentOutOfRangeException.ThrowIfLessThan(endedAt, startedAt);
        if (terminationReason is < ExecutableTerminationReason.Exited or > ExecutableTerminationReason.FailedToStart)
        {
            throw new ArgumentOutOfRangeException(nameof(terminationReason));
        }
        if ((terminationReason == ExecutableTerminationReason.Exited && exitCode is null) ||
            (terminationReason == ExecutableTerminationReason.FailedToStart && exitCode is not null))
        {
            throw new ArgumentException(ErrorMessages.InvalidResult, nameof(exitCode));
        }
        return new ExecutableResult
        {
            ExitCode = exitCode, StandardOutput = standardOutput, StandardError = standardError,
            StandardOutputTruncated = standardOutputTruncated, StandardErrorTruncated = standardErrorTruncated,
            StartedAt = startedAt.ToUniversalTime(), EndedAt = endedAt.ToUniversalTime(), TerminationReason = terminationReason
        };
    }
}

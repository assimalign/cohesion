using System;
using System.Globalization;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>A lock wait the probe recorded: one that lasted at least <see cref="SqlLockWaitProbe.SlowWait"/>.</summary>
/// <param name="Started">When the wait began, from the start of the run.</param>
/// <param name="Sequence">The waiting transaction.</param>
/// <param name="Resource">The resource it waited for, by kind and object.</param>
/// <param name="Mode">The mode it requested.</param>
/// <param name="Milliseconds">How long it waited.</param>
/// <param name="Outcome">How the wait ended.</param>
/// <param name="Worker">What the waiting worker was doing.</param>
/// <param name="Blockers">What the kernel reported blocking the wait while it waited, if it did.</param>
/// <param name="BlockerWorkers">What the workers of the blocking transactions were doing.</param>
internal sealed record SqlLockWait(TimeSpan Started, long Sequence, string Resource, string Mode, double Milliseconds, string Outcome,
    string Worker, string? Blockers, string? BlockerWorkers)
{
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"at {Started.TotalSeconds:F2} s transaction {Sequence} waited {Milliseconds:F0} ms for {Mode} on {Resource} ({Outcome}); {Worker}") +
            (Blockers is null ? "" : $"{Environment.NewLine}    blocked by {Blockers}{Environment.NewLine}    blockers: {BlockerWorkers}");
}

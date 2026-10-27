using System;

// Deviates from namespace-matches-assembly: model-local linked source.
namespace Assimalign.Cohesion.Database;

internal struct DatabasePurgeResult
{
    internal bool FullCompleted;
    internal bool UndoRetried;
    internal bool UndoPending;
    internal bool ReclamationPending;
    internal ulong Writer;
    internal Exception? Failure;
}

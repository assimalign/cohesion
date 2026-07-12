using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// In-memory transaction log for embedded working state and tests: appends are
/// trivially "durable" because the log shares the process lifetime.
/// </summary>
internal sealed class InMemoryTransactionLog : ITransactionLog
{
    private readonly List<(ulong Sequence, byte Kind)> _records = new();
    private readonly object _sync = new();

    private const byte begin = 1;
    private const byte commit = 2;
    private const byte abort = 3;

    /// <inheritdoc />
    public ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Append(sequence.Value, begin);
        return default;
    }

    /// <inheritdoc />
    public ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Append(sequence.Value, commit);
        return default;
    }

    /// <inheritdoc />
    public ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Append(sequence.Value, abort);
        return default;
    }

    private void Append(ulong sequence, byte kind)
    {
        lock (_sync)
        {
            _records.Add((sequence, kind));
        }
    }
}

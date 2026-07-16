using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// In-memory version store: per-entry version chains ordered oldest to newest,
/// resolved against snapshots newest-first. Used by engines for uncheckpointed
/// working state and by tests; page-backed stores arrive with the model engines.
/// </summary>
internal sealed class InMemoryVersionStore : IVersionStore
{
    private readonly Dictionary<(ulong ObjectId, ulong EntryId), List<Version>> _chains = new();
    private readonly object _sync = new();

    /// <inheritdoc />
    public ValueTask AppendVersionAsync(
        ulong objectId,
        ulong entryId,
        ReadOnlyMemory<byte> payload,
        TransactionSequence writer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var key = (objectId, entryId);

            if (!_chains.TryGetValue(key, out var chain))
            {
                chain = new List<Version>();
                _chains[key] = chain;
            }

            chain.Add(new Version(writer.Value, payload.ToArray()));
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>?> GetVisibleVersionAsync(
        ulong objectId,
        ulong entryId,
        TransactionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_chains.TryGetValue((objectId, entryId), out var chain))
            {
                return new ValueTask<ReadOnlyMemory<byte>?>((ReadOnlyMemory<byte>?)null);
            }

            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (snapshot.IsVisible(new TransactionSequence(chain[i].Writer)))
                {
                    return new ValueTask<ReadOnlyMemory<byte>?>(chain[i].Payload);
                }
            }

            return new ValueTask<ReadOnlyMemory<byte>?>((ReadOnlyMemory<byte>?)null);
        }
    }

    /// <inheritdoc />
    public ValueTask<long> PruneAsync(TransactionSequence oldestActive, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long pruned = 0;

        lock (_sync)
        {
            foreach (var chain in _chains.Values)
            {
                // Keep the newest version below the oldest-active bound (visible to
                // every current and future snapshot) and everything newer than it.
                int keepFrom = -1;

                for (int i = chain.Count - 1; i >= 0; i--)
                {
                    if (chain[i].Writer < oldestActive.Value)
                    {
                        keepFrom = i;
                        break;
                    }
                }

                if (keepFrom > 0)
                {
                    pruned += keepFrom;
                    chain.RemoveRange(0, keepFrom);
                }
            }
        }

        return new ValueTask<long>(pruned);
    }

    /// <inheritdoc />
    public ValueTask<long> PurgeWriterAsync(TransactionSequence writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long removed = 0;

        lock (_sync)
        {
            foreach (var chain in _chains.Values)
            {
                removed += chain.RemoveAll(version => version.Writer == writer.Value);
            }
        }

        return new ValueTask<long>(removed);
    }

    private readonly record struct Version(ulong Writer, byte[] Payload);
}

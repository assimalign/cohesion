using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Mapping;

internal sealed class MappingSession<TTransaction>(IMappingStore<TTransaction> store) : IMappingUnitOfWork<TTransaction>
    where TTransaction : class, IMappingTransaction
{
    private readonly List<ITrackedMapping<TTransaction>> _mappings = [];
    private bool _saving;
    private MappingCommitOutcomeUnknownException? _unresolvedCommit;

    public ITrackedEntities<TEntity, TKey> Register<TEntity, TKey, TSnapshot>(
        IEntityMapper<TEntity, TKey, TSnapshot> mapper,
        IEntityChangeWriter<TKey, TSnapshot, TTransaction> writer,
        IEqualityComparer<TKey>? keyComparer = null) where TEntity : class where TKey : notnull
    {
        EnsureIdle();
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(writer);
        var mapping = new TrackedEntities<TEntity, TKey, TSnapshot, TTransaction>(
            mapper, writer, keyComparer ?? EqualityComparer<TKey>.Default, EnsureIdle);
        _mappings.Add(mapping);
        return mapping;
    }

    public async ValueTask<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EnsureIdle();
        cancellationToken.ThrowIfCancellationRequested();
        _saving = true;
        try
        {
            // Capture every mapping before acquiring a store resource. A later mapper failure
            // cannot leave writes behind, and writers never receive live mutable entities.
            var changes = new List<IPendingChange<TTransaction>>();
            foreach (ITrackedMapping<TTransaction> mapping in _mappings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                mapping.Prepare(changes);
            }

            if (changes.Count == 0)
            {
                return 0;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await using TTransaction transaction = await store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (IPendingChange<TTransaction> change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await change.ApplyAsync(transaction, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MappingCommitOutcomeUnknownException exception)
            {
                // Record this before disposal: even a cleanup failure must never make an
                // unresolved save retryable or cause its snapshots to be accepted.
                _unresolvedCommit = exception;
                throw;
            }
            // Never observe cancellation between commit and acceptance. A committed save must
            // not be replayed merely because cancellation raced with its successful completion.
            foreach (IPendingChange<TTransaction> change in changes)
            {
                change.Accept();
            }

            return changes.Count;
        }
        finally
        {
            _saving = false;
        }
    }

    private void EnsureIdle()
    {
        if (_unresolvedCommit is not null)
        {
            throw new InvalidOperationException(
                "The mapping commit outcome is unknown. Reconcile the save with the store before creating a new unit of work.",
                _unresolvedCommit);
        }

        if (_saving)
        {
            throw new InvalidOperationException("A mapping save is already in progress.");
        }
    }
}

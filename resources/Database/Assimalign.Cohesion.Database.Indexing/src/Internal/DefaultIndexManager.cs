using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Default index manager: an in-memory directory of live B+Tree indexes over one
/// storage instance. Directory persistence is the model catalog's job — it exports
/// registrations (<see cref="IIndexRegistry"/>) and re-attaches them on open.
/// </summary>
internal sealed class DefaultIndexManager : IIndexManager, IIndexRegistry
{
    private readonly BTreeIndexManagerOptions _options;
    private readonly Dictionary<(ulong ObjectId, string Name), BTreeIndex> _indexes = new();
    private readonly object _sync = new();

    internal DefaultIndexManager(BTreeIndexManagerOptions options)
    {
        _options = options;

        if (options.ExistingIndexes is not null)
        {
            foreach (var registration in options.ExistingIndexes)
            {
                _indexes[(registration.ObjectId, registration.Definition.Name)] = new BTreeIndex(
                    options.Storage,
                    options.TransactionSource,
                    options.LockManager,
                    registration.ObjectId,
                    registration.Definition,
                    registration.RootPageId);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<IIndex> CreateIndexAsync(ITransactionContext transaction, ulong objectId, IndexDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        cancellationToken.ThrowIfCancellationRequested();

        if (definition.Kind != IndexKind.BTree)
        {
            throw new IndexException($"Index kind {definition.Kind} is not supported yet; only BTree indexes exist in the MVP.");
        }

        var storageTransaction = _options.TransactionSource.GetStorageTransaction(transaction);

        lock (_sync)
        {
            var key = (objectId, definition.Name);

            if (_indexes.ContainsKey(key))
            {
                throw new IndexException($"An index named '{definition.Name}' already exists on object {objectId}.");
            }

            long rootPageId = BTreeIndex.CreateRoot(_options.Storage, storageTransaction);
            var index = new BTreeIndex(
                _options.Storage, _options.TransactionSource, _options.LockManager, objectId, definition, rootPageId);
            _indexes[key] = index;

            return new ValueTask<IIndex>(index);
        }
    }

    /// <inheritdoc />
    public ValueTask DropIndexAsync(ITransactionContext transaction, ulong objectId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_indexes.Remove((objectId, name)))
            {
                throw new IndexException($"No index named '{name}' exists on object {objectId}.");
            }
        }

        // The tree's pages are left for the vacuum feature to reclaim — dropping is
        // a directory operation, not a physical walk.
        return default;
    }

    /// <inheritdoc />
    public bool TryGetIndex(ulong objectId, string name, out IIndex index)
    {
        lock (_sync)
        {
            if (_indexes.TryGetValue((objectId, name), out var found))
            {
                index = found;
                return true;
            }
        }

        index = null!;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<IIndex> GetIndexes(ulong objectId)
    {
        lock (_sync)
        {
            return _indexes.Where(pair => pair.Key.ObjectId == objectId).Select(pair => (IIndex)pair.Value).ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<BTreeIndexRegistration> ExportRegistrations()
    {
        lock (_sync)
        {
            return _indexes
                .Select(pair => new BTreeIndexRegistration(
                    pair.Key.ObjectId,
                    new IndexDefinition(pair.Value.Name, pair.Value.Kind, pair.Value.IsUnique),
                    pair.Value.RootPageId))
                .ToList();
        }
    }
}

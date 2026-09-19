using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Mapping;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Sql.Mapping;

/// <summary>Owns one SQL connection and buffers generated DML for a single atomic save.</summary>
/// <remarks>Inserts precede updates and deletes. Referenced parents are inserted before children;
/// children are deleted before parents. Cyclic dependencies are rejected before any DML.
/// A COMMIT exchange failure is explicitly unknown and permanently faults the owning store.</remarks>
public sealed class SqlMappingTransaction : IMappingTransaction
{
    private readonly SqlMappingStore _store;
    private readonly ISqlConnection _connection;
    private readonly List<PendingWrite> _writes = new();
    private bool _committed;
    private bool _disposed;
    private bool _committing;
    private bool _discard;
    private MappingCommitOutcomeUnknownException? _unknown;

    internal SqlMappingTransaction(SqlMappingStore store, ISqlConnection connection)
        => (_store, _connection) = (store, connection);

    /// <summary>Executes buffered DML and atomically commits the SQL transaction.</summary>
    /// <param name="cancellationToken">Cancels staging and the final check before COMMIT is sent.</param>
    /// <returns>The asynchronous save operation.</returns>
    /// <exception cref="InvalidOperationException">The transaction was used already, dependencies are cyclic, or a changed entity no longer exists.</exception>
    /// <exception cref="MappingCommitOutcomeUnknownException">The COMMIT response was not observed reliably.</exception>
    /// <remarks>Cancellation is not forwarded after COMMIT starts. A failed response is never
    /// interpreted as rollback, and no automatic retry is attempted.</remarks>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committing)
        {
            throw new InvalidOperationException("This SQL mapping transaction has already attempted commit.");
        }
        _committing = true;
        IReadOnlyList<PendingWrite> writes = OrderWrites();
        foreach (PendingWrite write in writes)
        {
            long affected = await _connection.ExecuteAsync(write.Command, cancellationToken).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new InvalidOperationException("A mapped change must affect exactly one row; the entity may have been changed or removed by another writer.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _connection.ExecuteAsync("COMMIT;", cancellationToken: CancellationToken.None).ConfigureAwait(false);
            _committed = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // The SQL wire protocol has no durable transaction identifier/status lookup. Even an
            // error response can follow server publication; conservatively keep the outcome unknown.
            _unknown = new MappingCommitOutcomeUnknownException(
                "The SQL COMMIT outcome is unknown. Do not retry this save. Reconcile authoritative database state before opening a new mapping scope.", exception);
            _store.Fault(_unknown);
            _discard = true;
            throw _unknown;
        }
    }

    /// <summary>Rolls back an uncommitted transaction and returns or closes its client connection.</summary>
    /// <returns>The asynchronous cleanup operation.</returns>
    /// <remarks>A rollback after an unknown COMMIT cannot establish whether the earlier transaction
    /// committed. Cleanup failures never replace an already reported unknown outcome.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            if (!_committed && !_discard && _connection.IsOpen)
            {
                try
                {
                    await _connection.ExecuteAsync("ROLLBACK;", cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    _store.Fault(exception);
                    _discard = true;
                    if (_unknown is null)
                    {
                        throw;
                    }
                }
            }
        }
        finally
        {
            _store.Release();
            try
            {
                if (_discard)
                {
                    await _connection.AbortAsync().ConfigureAwait(false);
                }
                else
                {
                    await _connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (_unknown is not null && exception is not OutOfMemoryException and not StackOverflowException)
            {
                // Preserve the outcome exception that the unit of work must see to reject replay.
            }
        }
    }

    internal void Stage(SqlMappingMetadata metadata, EntityChangeKind kind, SqlCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committing)
        {
            throw new InvalidOperationException("A committing transaction cannot stage more writes.");
        }
        _writes.Add(new(metadata, kind, command));
    }

    private IReadOnlyList<PendingWrite> OrderWrites()
    {
        var tables = new Dictionary<string, SqlMappingMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (PendingWrite write in _writes)
        {
            tables.TryAdd(write.Metadata.Table, write.Metadata);
        }
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int next = 0;
        foreach (string table in tables.Keys)
        {
            Visit(table);
        }
        var ordered = new List<(PendingWrite Write, int Index)>(_writes.Count);
        for (int index = 0; index < _writes.Count; index++)
        {
            ordered.Add((_writes[index], index));
        }
        ordered.Sort((left, right) =>
        {
            int kind = left.Write.Kind.CompareTo(right.Write.Kind);
            if (kind != 0)
            {
                return kind;
            }
            int rank = ranks[left.Write.Metadata.Table].CompareTo(ranks[right.Write.Metadata.Table]);
            if (left.Write.Kind == EntityChangeKind.Deleted)
            {
                rank = -rank;
            }
            return rank != 0 ? rank : left.Index.CompareTo(right.Index);
        });
        var result = new PendingWrite[ordered.Count];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = ordered[index].Write;
        }
        return result;

        void Visit(string table)
        {
            if (ranks.ContainsKey(table))
            {
                return;
            }
            if (!visiting.Add(table))
            {
                throw new InvalidOperationException("Cyclic or self-referencing SQL mapping table dependencies are unsupported; no DML was executed.");
            }
            foreach (string parent in tables[table].References)
            {
                if (tables.ContainsKey(parent))
                {
                    Visit(parent);
                }
            }
            visiting.Remove(table);
            ranks.Add(table, next++);
        }
    }

    private sealed record PendingWrite(SqlMappingMetadata Metadata, EntityChangeKind Kind, SqlCommand Command);
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Mapping;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Sql.Mapping;

/// <summary>Runs retained SQL mappings on the SQL client's transaction and query surface.</summary>
/// <remarks>The caller owns the client. Each save rents one connection and owns one explicit SQL
/// transaction. An uncertain commit permanently faults this store; create another only after
/// authoritative application reconciliation. Concurrent saves on one store are rejected.</remarks>
public sealed class SqlMappingStore : IMappingStore<SqlMappingTransaction>
{
    private readonly ISqlClient _client;
    private int _activeTransaction;
    private Exception? _failure;

    /// <summary>Creates a mapping store using an existing SQL client.</summary>
    /// <param name="client">The SQL client, owned by the caller.</param>
    /// <exception cref="ArgumentNullException">The client is null.</exception>
    public SqlMappingStore(ISqlClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>Begins a single-save transaction on a rented connection.</summary>
    /// <param name="cancellationToken">Cancels connection rental or transaction startup.</param>
    /// <returns>A transaction owned by the unit of work.</returns>
    /// <exception cref="InvalidOperationException">A transaction is already active or the store is faulted.</exception>
    public async ValueTask<SqlMappingTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfFaulted();
        if (Interlocked.CompareExchange(ref _activeTransaction, 1, 0) != 0)
        {
            throw new InvalidOperationException("A save transaction is already active on this SQL mapping store.");
        }
        ISqlConnection? connection = null;
        try
        {
            // A preceding save may have faulted and released the lease between the
            // initial guard and our acquisition. Never open a new transaction then.
            ThrowIfFaulted();
            connection = await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync("BEGIN;", cancellationToken: cancellationToken).ConfigureAwait(false);
            return new(this, connection);
        }
        catch
        {
            Release();
            if (connection is not null)
            {
                await connection.AbortAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>Executes a typed query and materializes entities using generated static access.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The immutable typed query.</param>
    /// <param name="cancellationToken">Cancels execution.</param>
    /// <returns>Detached entities; attach them to a registered set to track changes.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    /// <exception cref="ArgumentException">A predicate column is not part of its retained mapping or its comparison value has a different storage type.</exception>
    /// <exception cref="InvalidOperationException">The store has an unresolved transaction failure.</exception>
    /// <exception cref="NotSupportedException">A non-null comparison targets binary or floating-point data outside the engine's complete comparison domain.</exception>
    public async ValueTask<IReadOnlyList<TEntity>> QueryAsync<TEntity>(SqlQuery<TEntity> query,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfFaulted();
        SqlCommand command = query.ToCommand();
        await using ISqlConnection connection = await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        SqlResultSet result = await connection.QueryAsync(command, cancellationToken).ConfigureAwait(false);
        var entities = new TEntity[result.Count];
        for (int index = 0; index < result.Count; index++)
        {
            SqlRow row = result[index];
            var values = new object?[row.FieldCount];
            for (int column = 0; column < values.Length; column++)
            {
                values[column] = row[column];
            }
            entities[index] = query.Read(values);
        }
        return entities;
    }

    internal void Fault(Exception failure) => Interlocked.CompareExchange(ref _failure, failure, null);
    internal void Release() => Interlocked.Exchange(ref _activeTransaction, 0);

    private void ThrowIfFaulted()
    {
        Exception? failure = Volatile.Read(ref _failure);
        if (failure is not null)
        {
            throw new InvalidOperationException("The SQL mapping store is faulted. Resolve the transaction outcome before creating a new store; do not replay the save.", failure);
        }
    }
}

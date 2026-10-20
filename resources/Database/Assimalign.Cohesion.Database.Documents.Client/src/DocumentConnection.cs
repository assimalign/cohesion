using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Documents.Client.Internal;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client;

/// <summary>An authenticated Documents connection supporting one active operation at a time.</summary>
/// <remarks>Results own their data and are materialized before returning. Dispose returns a healthy
/// session to the shared pool. Explicit transactions over the wire are not supported.</remarks>
public sealed class DocumentConnection : IAsyncDisposable
{
    private readonly DatabaseConnection _connection;
    // Operation diagnostics may finish after disposal has returned the shared lease.
    private readonly string _database;
    private readonly object _operationLock = new();
    private Task? _disposeTask;
    private int _disposed;

    internal DocumentConnection(DatabaseConnection connection)
    {
        _connection = connection;
        _database = connection.Database;
    }

    /// <summary>Gets the database selected at the handshake.</summary>
    public string Database => _database;

    /// <summary>Gets whether this rental is open.</summary>
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _connection.IsOpen;

    /// <summary>Executes OQL and materializes positional rows or whole JSON documents.</summary>
    /// <param name="statement">The OQL statement.</param>
    /// <param name="parameters">Named JSON scalars or JsonElement values, without parameter prefixes.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>Columns, rows, whole documents when selected, and the affected count.</returns>
    /// <exception cref="ArgumentException">The statement or a parameter value is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The rental is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled; an unfinished exchange is discarded.</exception>
    /// <exception cref="DocumentClientException">The server refused the statement or the connection failed.</exception>
    public async ValueTask<DocumentResultSet> QueryAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
        => (await RunAsync("Query", new DocumentExchange(statement, parameters), cancellationToken).ConfigureAwait(false)).Result;

    /// <summary>Executes OQL and returns its affected count.</summary>
    /// <param name="statement">The OQL statement.</param>
    /// <param name="parameters">Named JSON scalars or JsonElement values.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The write count; -1 for queries and zero for index DDL.</returns>
    /// <exception cref="ArgumentException">The statement or a parameter value is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The rental is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="DocumentClientException">The statement or connection failed.</exception>
    public async ValueTask<long> ExecuteAsync(string statement, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
        => (await RunAsync("Execute", new DocumentExchange(statement, parameters), cancellationToken).ConfigureAwait(false)).Result.AffectedCount;

    /// <summary>Gets a document by identity, retaining its exact UTF-8 bytes and version.</summary>
    /// <param name="collection">The collection name.</param>
    /// <param name="id">The document identity.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The document, or null if absent.</returns>
    /// <exception cref="ArgumentException">The collection or identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">The rental is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="DocumentClientException">The operation or connection failed.</exception>
    public async ValueTask<Document?> GetAsync(string collection, DocumentId id, CancellationToken cancellationToken = default)
        => (await CommandAsync(DocumentProtocolOperation.Get, collection, id.Value, null, default, cancellationToken).ConfigureAwait(false)).Document;

    /// <summary>Inserts or replaces a complete JSON document, optionally checking its version.</summary>
    /// <param name="collection">The collection name.</param>
    /// <param name="id">The document identity.</param>
    /// <param name="content">The UTF-8 JSON, captured before the first await.</param>
    /// <param name="expectedVersion">The required existing version, or null for an unconditional write.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The stored document with its new version.</returns>
    /// <exception cref="ArgumentException">The collection or identity is blank.</exception>
    /// <exception cref="Protocol.ProtocolException">The content is not valid wire JSON.</exception>
    /// <exception cref="ObjectDisposedException">The rental is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled; its commit outcome may be unknown.</exception>
    /// <exception cref="DocumentClientException">The write was refused or the connection failed.</exception>
    public async ValueTask<Document> PutAsync(string collection, DocumentId id, ReadOnlyMemory<byte> content,
        DocumentVersion? expectedVersion = null, CancellationToken cancellationToken = default)
        => (await CommandAsync(DocumentProtocolOperation.Put, collection, id.Value, expectedVersion, content, cancellationToken).ConfigureAwait(false)).Document!.Value;

    /// <summary>Deletes a document, optionally checking its version.</summary>
    /// <param name="collection">The collection name.</param>
    /// <param name="id">The document identity.</param>
    /// <param name="expectedVersion">The required existing version, or null for an unconditional delete.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>True when a document was deleted, false if absent.</returns>
    /// <exception cref="ArgumentException">The collection or identity is blank.</exception>
    /// <exception cref="ObjectDisposedException">The rental is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled; its commit outcome may be unknown.</exception>
    /// <exception cref="DocumentClientException">The delete was refused or the connection failed.</exception>
    public async ValueTask<bool> DeleteAsync(string collection, DocumentId id, DocumentVersion? expectedVersion = null,
        CancellationToken cancellationToken = default)
        => (await CommandAsync(DocumentProtocolOperation.Delete, collection, id.Value, expectedVersion, default, cancellationToken).ConfigureAwait(false)).Result.AffectedCount != 0;

    /// <summary>Creates a collection in the bound database.</summary>
    /// <param name="name">The collection name.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The created collection name.</returns>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    /// <exception cref="ObjectDisposedException">The rental is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="DocumentClientException">The collection already exists, is reserved, or the connection failed.</exception>
    public async ValueTask<string> CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
        => (await CommandAsync(DocumentProtocolOperation.CreateCollection, name, "", null, default, cancellationToken).ConfigureAwait(false)).Collections[0];

    /// <summary>Looks up a collection in the bound database.</summary>
    /// <param name="name">The collection name.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The collection name.</returns>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    /// <exception cref="ObjectDisposedException">The rental is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="DocumentClientException">The collection is missing or the connection failed.</exception>
    public async ValueTask<string> GetCollectionAsync(string name, CancellationToken cancellationToken = default)
        => (await CommandAsync(DocumentProtocolOperation.GetCollection, name, "", null, default, cancellationToken).ConfigureAwait(false)).Collections[0];

    /// <summary>Drops a collection and its documents and indexes.</summary>
    /// <param name="name">The collection name.</param>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>The completion of the drop.</returns>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    /// <exception cref="ObjectDisposedException">The rental is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="DocumentClientException">The collection is missing or protected, or the connection failed.</exception>
    public async ValueTask DropCollectionAsync(string name, CancellationToken cancellationToken = default)
        => _ = await CommandAsync(DocumentProtocolOperation.DropCollection, name, "", null, default, cancellationToken).ConfigureAwait(false);

    /// <summary>Lists the bound database's collections.</summary>
    /// <param name="cancellationToken">Cancellation token for the exchange.</param>
    /// <returns>A materialized list in ordinal name order.</returns>
    /// <exception cref="ObjectDisposedException">The rental is disposed.</exception>
    /// <exception cref="InvalidOperationException">Another exchange is active.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    /// <exception cref="DocumentClientException">The operation or connection failed.</exception>
    public async ValueTask<IReadOnlyList<string>> GetCollectionsAsync(CancellationToken cancellationToken = default)
        => (await CommandAsync(DocumentProtocolOperation.ListCollections, "", "", null, default, cancellationToken).ConfigureAwait(false)).Collections;

    /// <summary>Closes this rental and discards its session.</summary>
    /// <returns>The noncancellable teardown.</returns>
    public ValueTask AbortAsync()
    {
        lock (_operationLock)
        {
            if (_disposeTask is not null) { return new(_disposeTask); }
            Volatile.Write(ref _disposed, 1);
            _disposeTask = _connection.AbortAsync().AsTask();
            return new(_disposeTask);
        }
    }

    /// <summary>Returns a healthy session to the pool, once only.</summary>
    /// <returns>The completion of the return.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_operationLock)
        {
            if (_disposeTask is not null) { return new(_disposeTask); }
            Volatile.Write(ref _disposed, 1);
            _disposeTask = _connection.DisposeAsync().AsTask();
            return new(_disposeTask);
        }
    }

    private ValueTask<DocumentExchangeResult> CommandAsync(DocumentProtocolOperation operation, string collection, string id,
        DocumentVersion? expectedVersion, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (operation != DocumentProtocolOperation.ListCollections) { ArgumentException.ThrowIfNullOrWhiteSpace(collection); }
        if (operation is DocumentProtocolOperation.Get or DocumentProtocolOperation.Put or DocumentProtocolOperation.Delete)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
        }
        return RunAsync(operation.ToString(), new DocumentExchange(new(operation, collection, id, expectedVersion, content)), cancellationToken);
    }

    private async ValueTask<DocumentExchangeResult> RunAsync(string operation, DocumentExchange exchange, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        long timestamp = DocumentClientEventSource.Log.GetTimestamp();
        bool started = DocumentClientEventSource.Log.QueryStart(this, operation);
        Exception? failure = null;
        DocumentExchangeResult? result = null;
        try
        {
            try
            {
                ValueTask<DocumentExchangeResult> execution;
                lock (_operationLock)
                {
                    // Register with the shared rental under the same lock as its return. In
                    // particular, QueryStart listeners can pause or dispose this wrapper.
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    execution = _connection.ExecuteAsync(exchange, cancellationToken);
                }
                return result = await execution.ConfigureAwait(false);
            }
            catch (DatabaseClientException exception)
            {
                throw new DocumentClientException(exception.Code, exception.Message, exception);
            }
        }
        catch (Exception exception) when (DocumentClientEventSource.CaptureFailure(exception, out failure))
        {
            // The filter observes the failure without catching it.
            throw;
        }
        finally
        {
            DocumentClientEventSource.Log.QueryEnded(this, operation, started, failure,
                result is null ? -1 : result.Result.Count + result.Collections.Count + (result.Document.HasValue ? 1 : 0), timestamp);
        }
    }
}

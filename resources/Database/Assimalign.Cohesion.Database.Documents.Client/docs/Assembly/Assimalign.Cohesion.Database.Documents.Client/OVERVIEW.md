# Public API

All client types are sealed concrete types. Constructors are internal except the options,
exception and column value type. `CT` below abbreviates `CancellationToken`; optional tokens
default to `default`. Normal framework enumeration members are shown without their explicit
non-generic implementations.

```csharp
public sealed class DocumentClient : IAsyncDisposable
{
    public static DocumentClient Create(DocumentClientOptions options);
    public DatabaseConnectionSettings Settings { get; }
    public ValueTask<DocumentConnection> ConnectAsync(CT cancellationToken = default);
    public ValueTask DisposeAsync();
}

public sealed class DocumentClientOptions
{
    public DatabaseConnectionSettings? Settings { get; set; }
    public IDuplexConnectionFactory? ConnectionFactory { get; set; }
}

public sealed class DocumentConnection : IAsyncDisposable
{
    public string Database { get; }
    public bool IsOpen { get; }
    public ValueTask<DocumentResultSet> QueryAsync(string statement,
        IReadOnlyDictionary<string, object?>? parameters = null, CT cancellationToken = default);
    public ValueTask<long> ExecuteAsync(string statement,
        IReadOnlyDictionary<string, object?>? parameters = null, CT cancellationToken = default);
    public ValueTask<Document?> GetAsync(string collection, DocumentId id, CT cancellationToken = default);
    public ValueTask<Document> PutAsync(string collection, DocumentId id, ReadOnlyMemory<byte> content,
        DocumentVersion? expectedVersion = null, CT cancellationToken = default);
    public ValueTask<bool> DeleteAsync(string collection, DocumentId id,
        DocumentVersion? expectedVersion = null, CT cancellationToken = default);
    public ValueTask<string> CreateCollectionAsync(string name, CT cancellationToken = default);
    public ValueTask<string> GetCollectionAsync(string name, CT cancellationToken = default);
    public ValueTask DropCollectionAsync(string name, CT cancellationToken = default);
    public ValueTask<IReadOnlyList<string>> GetCollectionsAsync(CT cancellationToken = default);
    public ValueTask AbortAsync();
    public ValueTask DisposeAsync();
}

public sealed class DocumentClientException : DatabaseException
{
    public DocumentClientException(ProtocolErrorCode code, string message,
        Exception? innerException = null, IReadOnlyList<Diagnostic>? diagnostics = null);
    public ProtocolErrorCode Code { get; }
    // Diagnostics is inherited from DatabaseException.
}

public sealed class DocumentResultSet : IReadOnlyList<DocumentRow>
{
    public IReadOnlyList<DocumentColumn> Columns { get; }
    public long AffectedCount { get; }
    public bool IsDocumentQuery { get; }
    public IReadOnlyList<JsonElement> Documents { get; }
    public int Count { get; }
    public DocumentRow this[int index] { get; }
    public IEnumerator<DocumentRow> GetEnumerator();
}

public sealed class DocumentRow : IReadOnlyList<object?>
{
    public int Count { get; }
    public object? this[int index] { get; }
    public object? this[string name] { get; }
    public IEnumerator<object?> GetEnumerator();
}

public sealed record DocumentColumn(string Name, int Ordinal, DatabaseType Type);
```

Create requires settings and a factory. Connect rents one authenticated database-bound session.
Argument and disposed-rental errors remain normal CLR exceptions; failed dialing, authentication,
protocol and statement execution surface as `DocumentClientException`. Cancellation remains
`OperationCanceledException`. Concurrent exchanges fail with `InvalidOperationException`.
Result indexers use normal out-of-range and missing-name exceptions. DocumentId, DocumentVersion
and Document are reused from Database.Documents, and Diagnostic from Database.Types.
The exception's inherited Diagnostics carrier preserves all retained diagnostic fields and
source ordering; Message is the server's composed text with each code prefixed once.

The companion server types live in `Assimalign.Cohesion.Database.Documents`:

```csharp
public sealed class DocumentDatabaseServer : DatabaseServer
{
    public static DocumentDatabaseServer Create(DocumentDatabaseEngine engine,
        DocumentDatabaseServerOptions options);
    public new DocumentDatabaseEngine Engine { get; }
    public override IReadOnlyCollection<DatabaseServerSession> Sessions { get; }
    // StartAsync, StopAsync, DisposeAsync and lifecycle state are inherited.
}

public sealed class DocumentDatabaseServerOptions
{
    public IDuplexConnectionListener? Listener { get; set; }
    public DatabaseAuthenticator? Authenticator { get; set; }
    public int MaxSessions { get; set; } // 100
    public TimeSpan AuthenticationTimeout { get; set; } // 10 seconds
    public TimeSpan IdleTimeout { get; set; } // 30 minutes
    public TimeSpan ShutdownDrainTimeout { get; set; } // 30 seconds
}

// Added beside the existing factory overload on the sealed engine builder:
public DocumentDatabaseEngineBuilder AddServer(Action<DocumentDatabaseServerOptions> configure);
public DocumentDatabaseEngineBuilder AddServer(Func<DocumentDatabaseEngine, DatabaseServer> factory);
```

Create snapshots options and rejects a missing listener, nonpositive session limit or invalid timer
duration. Standalone server disposal releases its listener and sessions; engine disposal owns
servers attached through the builder. The server-session implementation remains internal.

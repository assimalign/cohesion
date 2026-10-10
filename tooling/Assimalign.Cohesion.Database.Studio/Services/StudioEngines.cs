using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.KeyValuePair;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>
/// Owns one in-process engine per model (each on its own sub-folder of the data root) and the
/// optional loopback servers the Studio starts for wire mode. Each engine is held as the typed
/// engine its model's builder returns, so nothing downcasts from <see cref="DatabaseEngine"/>.
/// </summary>
internal sealed class StudioEngines : IAsyncDisposable
{
    /// <summary>
    /// The database every engine's builder declares: the engine creates it on its first build over a
    /// data root and opens it on later ones, and refuses to drop it while the declaration stands.
    /// </summary>
    public const string DeclaredDatabase = "studio";

    private readonly Dictionary<StudioModel, (DatabaseServer Server, TcpConnectionListener Listener)> _servers = [];
    private SqlDatabaseEngine? _sql;
    private DocumentDatabaseEngine? _documents;
    private GraphDatabaseEngine? _graph;
    private KeyValueDatabaseEngine? _keyValue;
    private BlobDatabaseEngine? _blob;

    private StudioEngines(string dataRoot)
    {
        DataRoot = dataRoot;
    }

    public string DataRoot { get; }

    public SqlDatabaseEngine Sql => _sql ?? throw Disposed();

    public DocumentDatabaseEngine Documents => _documents ?? throw Disposed();

    public GraphDatabaseEngine Graph => _graph ?? throw Disposed();

    public KeyValueDatabaseEngine KeyValue => _keyValue ?? throw Disposed();

    public BlobDatabaseEngine Blob => _blob ?? throw Disposed();

    public string GetModelRoot(StudioModel model) => System.IO.Path.Combine(DataRoot, model.FolderName);

    /// <summary>
    /// Creates all five engines on file storage under <paramref name="dataRoot"/>, each composed
    /// through its model's engine builder: named once, with its options on the builder, declaring
    /// <see cref="DeclaredDatabase"/>, and (SQL) with the Studio's registered functions and the
    /// declared database's schema.
    /// </summary>
    public static StudioEngines Create(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        dataRoot = System.IO.Path.GetFullPath(dataRoot);
        var engines = new StudioEngines(dataRoot);

        try
        {
            foreach (StudioModel model in Enum.GetValues<StudioModel>())
            {
                Directory.CreateDirectory(engines.GetModelRoot(model));
            }

            SqlDatabaseEngineBuilder sql = SqlDatabaseEngine.CreateBuilder("studio-sql");
            sql.Options.RootPath = engines.GetModelRoot(StudioModel.Sql);
            sql.AddStudioFunctions().AddStudioDatabase(DeclaredDatabase);
            engines._sql = sql.Build();

            DocumentDatabaseEngineBuilder documents = DocumentDatabaseEngine.CreateBuilder("studio-documents");
            documents.Options.RootPath = engines.GetModelRoot(StudioModel.Documents);
            documents.AddDatabase(DeclaredDatabase);
            engines._documents = documents.Build();

            GraphDatabaseEngineBuilder graph = GraphDatabaseEngine.CreateBuilder("studio-graph");
            graph.Options.RootPath = engines.GetModelRoot(StudioModel.Graph);
            graph.AddDatabase(DeclaredDatabase);
            engines._graph = graph.Build();

            KeyValueDatabaseEngineBuilder keyValue = KeyValueDatabaseEngine.CreateBuilder("studio-keyvalue");
            keyValue.Options.RootPath = engines.GetModelRoot(StudioModel.KeyValue);
            keyValue.AddDatabase(DeclaredDatabase);
            engines._keyValue = keyValue.Build();

            BlobDatabaseEngineBuilder blob = BlobDatabaseEngine.CreateBuilder("studio-blob");
            blob.Options.RootPath = engines.GetModelRoot(StudioModel.Blob);
            blob.AddDatabase(DeclaredDatabase);
            engines._blob = blob.Build();
        }
        catch
        {
            engines.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        return engines;
    }

    public IPEndPoint? GetServerEndPoint(StudioModel model)
        => _servers.TryGetValue(model, out var entry) && entry.Listener.EndPoint is IPEndPoint { Port: > 0 } endPoint ? endPoint : null;

    /// <summary>Starts the model's server on 127.0.0.1:<paramref name="port"/> (0 = OS-assigned) and waits for the bind.</summary>
    public async Task<IPEndPoint> StartServerAsync(StudioModel model, int port, CancellationToken cancellationToken = default)
    {
        if (!model.HasWireServer)
        {
            throw new NotSupportedException($"{model.DisplayName} has no wire server.");
        }

        if (GetServerEndPoint(model) is { } existing)
        {
            return existing;
        }

        TcpConnectionListener listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, port));
        TimeSpan drain = TimeSpan.FromSeconds(2);
        DatabaseServer server = model switch
        {
            StudioModel.Sql => SqlDatabaseServer.Create(Sql, new SqlDatabaseServerOptions { Listener = listener, Authenticator = null, ShutdownDrainTimeout = drain }),
            StudioModel.Graph => GraphDatabaseServer.Create(Graph, new GraphDatabaseServerOptions { Listener = listener, Authenticator = null, ShutdownDrainTimeout = drain }),
            StudioModel.KeyValue => KeyValueDatabaseServer.Create(KeyValue, new KeyValueDatabaseServerOptions { Listener = listener, Authenticator = null, ShutdownDrainTimeout = drain }),
            StudioModel.Blob => BlobDatabaseServer.Create(Blob, new BlobDatabaseServerOptions { Listener = listener, Authenticator = null, ShutdownDrainTimeout = drain }),
            _ => throw new NotSupportedException(model.ToString()),
        };

        _servers[model] = (server, listener);
        await server.StartAsync(cancellationToken).ConfigureAwait(false);

        // Some listeners bind lazily on the first accept; poll until the OS port is observable.
        long deadline = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            if (listener.EndPoint is IPEndPoint { Port: > 0 } bound)
            {
                return bound;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"The {model.DisplayName} loopback listener did not bind within 15 seconds.");
    }

    public async Task StopServerAsync(StudioModel model)
    {
        if (!_servers.Remove(model, out var entry))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await entry.Server.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: a throw-away tool must still release the engine below.
        }

        try
        {
            await entry.Server.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (StudioModel model in _servers.Keys.ToArray())
        {
            await StopServerAsync(model).ConfigureAwait(false);
        }

        // Typed fields upcast to the root for a uniform best-effort dispose; a field is null when
        // Create failed before its engine was built.
        DatabaseEngine?[] engines = [_sql, _documents, _graph, _keyValue, _blob];
        _sql = null;
        _documents = null;
        _graph = null;
        _keyValue = null;
        _blob = null;

        foreach (DatabaseEngine? engine in engines)
        {
            if (engine is null)
            {
                continue;
            }

            try
            {
                await engine.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private static ObjectDisposedException Disposed() => new(nameof(StudioEngines));
}

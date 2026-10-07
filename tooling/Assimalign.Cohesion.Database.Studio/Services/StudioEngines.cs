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
/// optional loopback servers the Studio starts for wire mode.
/// </summary>
internal sealed class StudioEngines : IAsyncDisposable
{
    private readonly Dictionary<StudioModel, DatabaseEngine> _engines = [];
    private readonly Dictionary<StudioModel, (DatabaseServer Server, TcpConnectionListener Listener)> _servers = [];

    private StudioEngines(string dataRoot)
    {
        DataRoot = dataRoot;
    }

    public string DataRoot { get; }

    public SqlDatabaseEngine Sql => (SqlDatabaseEngine)_engines[StudioModel.Sql];

    public DocumentDatabaseEngine Documents => (DocumentDatabaseEngine)_engines[StudioModel.Documents];

    public GraphDatabaseEngine Graph => (GraphDatabaseEngine)_engines[StudioModel.Graph];

    public KeyValueDatabaseEngine KeyValue => (KeyValueDatabaseEngine)_engines[StudioModel.KeyValue];

    public BlobDatabaseEngine Blob => (BlobDatabaseEngine)_engines[StudioModel.Blob];

    public DatabaseEngine Get(StudioModel model) => _engines[model];

    public string GetModelRoot(StudioModel model) => System.IO.Path.Combine(DataRoot, model.FolderName);

    /// <summary>Creates all five engines on file storage under <paramref name="dataRoot"/>.</summary>
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

            engines._engines[StudioModel.Sql] = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
            {
                EngineName = "studio-sql",
                RootPath = engines.GetModelRoot(StudioModel.Sql),
            });
            engines._engines[StudioModel.Documents] = DocumentDatabaseEngine.Create(new DocumentDatabaseEngineOptions
            {
                EngineName = "studio-documents",
                RootPath = engines.GetModelRoot(StudioModel.Documents),
            });
            engines._engines[StudioModel.Graph] = GraphDatabaseEngine.Create(new GraphDatabaseEngineOptions
            {
                EngineName = "studio-graph",
                RootPath = engines.GetModelRoot(StudioModel.Graph),
            });
            engines._engines[StudioModel.KeyValue] = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions
            {
                EngineName = "studio-keyvalue",
                RootPath = engines.GetModelRoot(StudioModel.KeyValue),
            });
            engines._engines[StudioModel.Blob] = BlobDatabaseEngine.Create(new BlobDatabaseEngineOptions
            {
                EngineName = "studio-blob",
                RootPath = engines.GetModelRoot(StudioModel.Blob),
            });
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

        foreach (DatabaseEngine engine in _engines.Values)
        {
            try
            {
                await engine.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _engines.Clear();
    }
}

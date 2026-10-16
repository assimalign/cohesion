using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

internal sealed class DocumentClientTestHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(40));

    private DocumentClientTestHarness(DocumentDatabaseEngine engine, DocumentDatabase database,
        IDuplexConnectionListener listener, IDuplexConnectionFactory factory, DocumentDatabaseServer server, DocumentClient client)
    {
        Engine = engine;
        Database = database;
        Listener = listener;
        Factory = factory;
        Server = server;
        Client = client;
    }

    internal DocumentDatabaseEngine Engine { get; }
    internal DocumentDatabase Database { get; }
    internal IDuplexConnectionListener Listener { get; }
    internal IDuplexConnectionFactory Factory { get; }
    internal DocumentDatabaseServer Server { get; }
    internal DocumentClient Client { get; }
    internal CancellationToken Token => _timeout.Token;

    internal static async Task<DocumentClientTestHarness> StartAsync(bool tcp = false, Action<DocumentDatabaseServerOptions>? configure = null)
    {
        var builder = DocumentDatabaseEngine.CreateBuilder("documents-wire").AddDatabase("documents");
        IDuplexConnectionListener listener;
        IDuplexConnectionFactory factory;
        if (tcp)
        {
            listener = new TcpConnectionListener(new TcpConnectionListenerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
            factory = new TcpConnectionFactory();
        }
        else
        {
            var memory = new InMemoryConnectionListener();
            listener = memory;
            factory = memory.CreateFactory();
        }
        builder.AddServer(options =>
        {
            options.Listener = listener;
            options.ShutdownDrainTimeout = TimeSpan.FromSeconds(2);
            configure?.Invoke(options);
        });
        var engine = await builder.BuildAsync(CancellationToken.None);
        var server = (DocumentDatabaseServer)engine.Servers.Single();
        try
        {
            await server.StartAsync(CancellationToken.None);
            var client = DocumentClient.Create(new()
            {
                Settings = new DatabaseConnectionSettings { Database = "documents", Principal = "tester", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
                ConnectionFactory = factory,
            });
            return new(engine, await engine.OpenDatabaseAsync("documents"), listener, factory, server, client);
        }
        catch
        {
            await engine.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Client.DisposeAsync(); }
        finally
        {
            try { await Engine.DisposeAsync(); }
            finally { _timeout.Dispose(); }
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Sql.Mapping.Tests;

internal sealed class SqlMapperTestHarness : IAsyncDisposable
{
    private SqlMapperTestHarness(SqlDatabaseEngine engine, IDatabase database,
        InMemoryConnectionListener listener, SqlDatabaseServer server, ISqlClient client)
    {
        Engine = engine;
        Database = database;
        Listener = listener;
        Server = server;
        Client = client;
        Store = new SqlMappingStore(client);
    }

    internal SqlDatabaseEngine Engine { get; }

    internal IDatabase Database { get; }

    internal InMemoryConnectionListener Listener { get; }

    internal SqlDatabaseServer Server { get; }

    internal ISqlClient Client { get; }

    internal SqlMappingStore Store { get; }

    internal static async Task<SqlMapperTestHarness> StartAsync(CancellationToken cancellationToken)
    {
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-mapper-acceptance" });
        IDatabase database = await engine.CreateDatabaseAsync("mapping_tests", cancellationToken);
        await ((IDatabaseSchemaProvisioner)database).ApplySchemaAsync(MapperSchema.Declare(), cancellationToken: cancellationToken);
        var listener = new InMemoryConnectionListener();
        var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync(cancellationToken);
        ISqlClient client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = "mapping_tests",
                Principal = "mapper-tests",
                EndPoint = listener.EndPoint,
            },
            ConnectionFactory = listener.CreateFactory(),
        });
        return new SqlMapperTestHarness(engine, database, listener, server, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
        await Listener.DisposeAsync();
        await Engine.DisposeAsync();
    }
}

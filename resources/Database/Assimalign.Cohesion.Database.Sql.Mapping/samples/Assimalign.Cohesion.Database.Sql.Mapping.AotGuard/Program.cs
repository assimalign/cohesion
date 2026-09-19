using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Mapping;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Sql.Mapping.AotGuard;

internal static class Program
{
    private static async Task Main()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken token = timeout.Token;
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-mapper-aot" });
        IDatabase database = await engine.CreateDatabaseAsync("mapping_guard", token);
        await ((IDatabaseSchemaProvisioner)database).ApplySchemaAsync(GuardSchema.Deployment(), cancellationToken: token);
        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync(token);
        await using ISqlClient client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = "mapping_guard",
                Principal = "mapper-aot",
                EndPoint = listener.EndPoint,
            },
            ConnectionFactory = listener.CreateFactory(),
        });
        var store = new SqlMappingStore(client);
        var work = MappingUnitOfWork.Create(store);
        var children = SqlMapping.Register(work, new GuardChildMapper());
        var parents = SqlMapping.Register(work, new GuardParentMapper());
        var parent = new GuardParent { Id = 1, Name = "parent" };
        var retained = new GuardChild { Id = 10, ParentId = 1, Name = "before", Payload = [1, 2] };
        var removed = new GuardChild { Id = 11, ParentId = 1, Name = "remove" };
        parents.Add(parent);
        children.Add(retained);
        children.Add(removed);
        Require(await work.SaveChangesAsync(token) == 3, "The foreign-key graph was not saved.");

        var graph = await store.QueryAsync(SqlMapping.Query(new GuardChildMapper())
            .Where(GuardChildMapper.Columns.ParentId.Equal(1)).OrderBy(GuardChildMapper.Columns.Id), token);
        Require(graph.Count == 2 && graph[0].Id == 10 && graph[1].Id == 11 && graph[0].ParentId == parent.Id,
            "Generated graph materialization failed.");
        Require(graph[0].Payload is byte[] bytes && bytes.AsSpan().SequenceEqual(new byte[] { 1, 2 }),
            "Generated binary materialization failed.");

        retained.Name = "after";
        retained.Payload![0] = 9;
        children.Remove(removed);
        children.Add(new GuardChild { Id = 12, ParentId = 1, Name = "inserted" });
        Require(await work.SaveChangesAsync(token) == 3, "Mixed insert, update and delete did not commit together.");
        var query = SqlMapping.Query(new GuardChildMapper())
            .Where(GuardChildMapper.Columns.Id.GreaterThanOrEqual(10).And(GuardChildMapper.Columns.Name.IsNotNull()))
            .Distinct().OrderBy(GuardChildMapper.Columns.ParentId).ThenBy(GuardChildMapper.Columns.Id).Skip(0).Take(1);
        var selected = await store.QueryAsync(query, token);
        Require(selected.Count == 1 && selected[0].Name == "after" && selected[0].Payload![0] == 9,
            "Typed SQL query or scalar/binary tracking failed.");
        Require(children.Find(11) is null && await work.SaveChangesAsync(token) == 0,
            "Successful save did not accept identity and snapshot changes.");

        parents.Add(new GuardParent { Id = 2, Name = "must roll back" });
        var invalid = new GuardChild { Id = 20, ParentId = 999, Name = "invalid foreign key" };
        children.Add(invalid);
        bool rejected = false;
        try
        {
            await work.SaveChangesAsync(token);
        }
        catch (SqlClientException)
        {
            rejected = true;
        }
        Require(rejected, "The real engine did not enforce the retained foreign key.");
        Require((await store.QueryAsync(SqlMapping.Query(new GuardParentMapper()), token)).Count == 1,
            "A failed save published an earlier parent insert.");
        invalid.ParentId = 2;
        Require(await work.SaveChangesAsync(token) == 2, "Rollback lost retryable changes.");

        await using (var connection = await client.ConnectAsync(token))
        {
            bool protectedTable = false;
            try
            {
                await connection.ExecuteAsync("DROP TABLE guard_children", cancellationToken: token);
            }
            catch (SqlClientException exception) when (exception.Message.Contains("schema", StringComparison.OrdinalIgnoreCase))
            {
                protectedTable = true;
            }
            Require(protectedTable, "Mapper use bypassed retained schema ownership.");
        }
        Console.WriteLine("SQL mapper NativeAOT guard passed: generated schema, foreign-key graph, typed queries, scalar/binary tracking, mixed save, atomic rollback, retry and schema ownership.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

using System;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Database.Storage;
using Example.AppC.Database;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

builder.AddSql("appc-sql", sql =>
{
    sql.Options.RootPath = Resource.Mounts.Data.Path
        ?? throw new InvalidOperationException("The database data mount must have a materialized path.");
    sql.Options.Durability = Resource.Settings.DatabaseDurability.Get<StorageCommitDurability>();
    sql.AddServer(server => server.Listen(Resource.Endpoints.Db));

    // The engine's build creates the database when it does not exist and applies this schema
    // before the server accepts its first connection.
    sql.AddDatabase("inventory", database => database.Schema(schema =>
    {
        schema.Table<Item>("Items", table =>
        {
            table.Key(item => item.Sku);
            table.Index(item => item.Name);
        });
        schema.Table<Movement>("Movements", table =>
        {
            table.Key(movement => movement.Id);
            table.References<Item>(movement => movement.Sku);
        });
    }));
});

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

internal sealed record Item(string Sku, string Name, int OnHand, int Reserved, DateTime UpdatedAt);

internal sealed record Movement(long Id, string Sku, int Delta, string Reason, DateTime MovedAt);

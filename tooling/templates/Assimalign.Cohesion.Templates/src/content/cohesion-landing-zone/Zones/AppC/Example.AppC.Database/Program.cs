using System;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Hosting;
using Example.AppC.Database;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

await using SqlDatabaseEngine engine = builder.AddSqlDatabase(options =>
{
    options.EngineName = "appc-sql";
    options.RootPath = Resource.Mounts.Data.Path
        ?? throw new InvalidOperationException("The database data mount must have a materialized path.");
    options.Durability = Resource.Settings.DatabaseDurability.Get<StorageCommitDurability>();
});

builder.AddDatabase(engine, "inventory", database =>
{
    database.Table<Item>(table =>
    {
        table.Key(item => item.Sku);
        table.Index(item => item.Name);
    });
    database.Table<Movement>(table =>
    {
        table.Key(movement => movement.Id);
        table.References<Item>(movement => movement.Sku);
    });
    database.Principal(
        "appc-api",
        principal => principal.Grant(Permission.ReadWrite, "Items", "Movements"));
});

builder.AddSqlServer(engine, options => options.Listen(Resource.Endpoints.Db));

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

internal sealed record Item(string Sku, string Name, int OnHand, int Reserved, DateTime UpdatedAt);

internal sealed record Movement(long Id, string Sku, int Delta, string Reason, DateTime MovedAt);

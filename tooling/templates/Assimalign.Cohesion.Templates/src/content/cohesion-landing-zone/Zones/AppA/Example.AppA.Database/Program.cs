using System;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Hosting;
using Example.AppA.Database;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

await using SqlDatabaseEngine engine = builder.AddSqlDatabase(options =>
{
    options.EngineName = "appa-sql";
    options.RootPath = Resource.Mounts.Data.Path
        ?? throw new InvalidOperationException("The database data mount must have a materialized path.");
    options.Durability = Resource.Settings.DatabaseDurability.Get<StorageCommitDurability>();
});

builder.AddDatabase(engine, "orders", database =>
{
    database.Table<Order>(table =>
    {
        table.Key(order => order.Id);
        table.Index(order => order.CustomerId);
    });
    database.Table<OrderLine>(table =>
    {
        table.Key(line => line.Id);
        table.References<Order>(line => line.OrderId);
    });
    database.Principal(
        "appa-api",
        principal => principal.Grant(Permission.ReadWrite, "Orders", "OrderLines"));
});

builder.AddSqlServer(engine, options => options.Listen(Resource.Endpoints.Db));

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

internal sealed record Order(long Id, long CustomerId, DateTime PlacedAt, string Status, decimal Total);

internal sealed record OrderLine(long Id, long OrderId, string Sku, int Quantity, decimal UnitPrice);

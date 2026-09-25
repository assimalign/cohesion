using System;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Database.Storage;
using Example.AppA.Database;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

builder.AddSql((_, options) =>
{
    options.EngineName = "appa-sql";
    options.RootPath = Resource.Mounts.Data.Path
        ?? throw new InvalidOperationException("The database data mount must have a materialized path.");
    options.Durability = Resource.Settings.DatabaseDurability.Get<StorageCommitDurability>();
    options.AddServer(engine => SqlDatabaseServer.Create(
        (SqlDatabaseEngine)engine, new SqlDatabaseServerOptions().Listen(Resource.Endpoints.Db)));
});

SqlCompiledSchema schema = SqlSchema.Compile("orders", database =>
{
    database.Table<Order>("Orders", table =>
    {
        table.Key(order => order.Id);
        table.Index(order => order.CustomerId);
    });
    database.Table<OrderLine>("OrderLines", table =>
    {
        table.Key(line => line.Id);
        table.References<Order>(line => line.OrderId);
    });
    database.Principal(
        "appa-api",
        principal => principal.Grant(SqlPermission.ReadWrite, "Orders", "OrderLines"));
});

builder.AddDatabase("appa-sql", "orders", schema);

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

internal sealed record Order(long Id, long CustomerId, DateTime PlacedAt, string Status, decimal Total);

internal sealed record OrderLine(long Id, long OrderId, string Sku, int Quantity, decimal UnitPrice);

using System;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Database.Storage;
using Example.AppA.Database;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

builder.AddSql("appa-sql", sql =>
{
    sql.Options.RootPath = Resource.Mounts.Data.Path
        ?? throw new InvalidOperationException("The database data mount must have a materialized path.");
    sql.Options.Durability = Resource.Settings.DatabaseDurability.Get<StorageCommitDurability>();
    sql.AddServer(server => server.Listen(Resource.Endpoints.Db));

    // The engine's build creates the database when it does not exist and applies this schema
    // before the server accepts its first connection. Every member a table stores is declared:
    // Key, Column, Index and References each add the member they select as a column.
    sql.AddDatabase("orders", database => database.Schema(schema =>
    {
        schema.Table<Order>("Orders", table =>
        {
            table.Key(order => order.Id);
            table.Column(order => order.CustomerId);
            table.Column(order => order.PlacedAt);
            table.Column(order => order.Status);
            table.Column(order => order.Total);
            table.Index(order => order.CustomerId);
        });
        schema.Table<OrderLine>("OrderLines", table =>
        {
            table.Key(line => line.Id);
            table.Column(line => line.OrderId);
            table.Column(line => line.Sku);
            table.Column(line => line.Quantity);
            table.Column(line => line.UnitPrice);
            table.References<Order>(line => line.OrderId);
        });
    }));
});

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

internal sealed record Order(long Id, long CustomerId, DateTime PlacedAt, string Status, decimal Total);

internal sealed record OrderLine(long Id, long OrderId, string Sku, int Quantity, decimal UnitPrice);

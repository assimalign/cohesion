using System;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.SampleHost;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Hosting;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

await using SqlDatabaseEngine engine = builder.AddSqlDatabase(options =>
{
    options.EngineName = "sample-sql";
    options.RootPath = Resource.Mounts.Data.Path
        ?? throw new InvalidOperationException("The Database data mount must have a materialized path.");
    options.Durability = Resource.Settings.DatabaseDurability.Get<StorageCommitDurability>();
});

builder.AddDatabase(engine, "sample", database =>
{
    database.Type<Money>(type => type.Decimal(18, 2));
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
    database.Function(
        "order_total",
        (long orderId) => Sql.Sum<OrderLine>(
            line => line.Quantity * line.UnitPrice,
            line => line.OrderId == orderId));
    database.Trigger<Order>(
        TriggerEvent.AfterInsert,
        (transaction, order) => transaction.Audit("order.placed", order.Id));
    database.Principal("sample-client", principal => principal.Grant(
        Permission.ReadWrite,
        "Orders",
        "OrderLines"));
});

builder.AddSqlServer(engine, options => options.Listen(Resource.Endpoints.Db));

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

/// <summary>
/// Exposes the compiler-generated top-level entry point to in-process test factories.
/// </summary>
public partial class Program;

internal sealed record Order(long Id, long CustomerId, Money Total);

internal sealed record OrderLine(long Id, long OrderId, int Quantity, Money UnitPrice);

internal readonly record struct Money(decimal Amount)
{
    public static Money operator *(int quantity, Money value)
        => new(quantity * value.Amount);
}

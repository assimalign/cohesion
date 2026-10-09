using System;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Database.Storage;
using Example.AppB.Database;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

builder.AddSql("appb-sql", sql =>
{
    sql.Options.RootPath = Resource.Mounts.Data.Path
        ?? throw new InvalidOperationException("The database data mount must have a materialized path.");
    sql.Options.Durability = Resource.Settings.DatabaseDurability.Get<StorageCommitDurability>();
    sql.AddServer(server => server.Listen(Resource.Endpoints.Db));

    // The engine's build creates the database when it does not exist and applies this schema
    // before the server accepts its first connection.
    sql.AddDatabase("billing", database => database.Schema(schema =>
    {
        schema.Table<Invoice>("Invoices", table =>
        {
            table.Key(invoice => invoice.Id);
            table.Index(invoice => invoice.AccountId);
        });
        schema.Table<Payment>("Payments", table =>
        {
            table.Key(payment => payment.Id);
            table.References<Invoice>(payment => payment.InvoiceId);
        });
    }));
});

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

internal sealed record Invoice(long Id, long AccountId, DateTime IssuedAt, DateTime DueAt, decimal Amount, bool Paid);

internal sealed record Payment(long Id, long InvoiceId, DateTime ReceivedAt, decimal Amount);

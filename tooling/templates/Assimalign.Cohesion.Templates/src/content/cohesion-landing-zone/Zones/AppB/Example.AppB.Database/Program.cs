using System;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Storage;
using Example.AppB.Database;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

await using SqlDatabaseEngine engine = builder.AddSqlDatabase(options =>
{
    options.EngineName = "appb-sql";
    options.RootPath = Resource.Mounts.Data.Path
        ?? throw new InvalidOperationException("The database data mount must have a materialized path.");
    options.Durability = Resource.Settings.DatabaseDurability.Get<StorageCommitDurability>();
});

SqlCompiledSchema schema = SqlSchema.Compile("billing", database =>
{
    database.Table<Invoice>("Invoices", table =>
    {
        table.Key(invoice => invoice.Id);
        table.Index(invoice => invoice.AccountId);
    });
    database.Table<Payment>("Payments", table =>
    {
        table.Key(payment => payment.Id);
        table.References<Invoice>(payment => payment.InvoiceId);
    });
    database.Principal(
        "appb-api",
        principal => principal.Grant(SqlPermission.ReadWrite, "Invoices", "Payments"));
});

builder.AddDatabase(engine, "billing", schema);

builder.AddSqlServer(engine, options => options.Listen(Resource.Endpoints.Db));

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

internal sealed record Invoice(long Id, long AccountId, DateTime IssuedAt, DateTime DueAt, decimal Amount, bool Paid);

internal sealed record Payment(long Id, long InvoiceId, DateTime ReceivedAt, decimal Amount);

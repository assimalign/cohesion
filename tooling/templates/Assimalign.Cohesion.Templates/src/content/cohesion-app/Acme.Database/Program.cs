using System;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;
using Acme.Database;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

builder.AddSql("acme-sql", sql =>
{
    sql.Options.RootPath = Resource.Mounts.Data.Path
        ?? throw new InvalidOperationException("The database data mount must have a materialized path.");
    sql.AddServer(server => server.Listen(Resource.Endpoints.Db));

    // The engine's build creates the database when it does not exist and applies this schema
    // before the server accepts its first connection.
    sql.AddDatabase("customers", database => database.Schema(schema =>
    {
        schema.Table<Customer>("Customers", table =>
        {
            table.Key(customer => customer.Id);
            table.Index(customer => customer.Email);
        });
    }));
});

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

internal sealed record Customer(long Id, string Name, string Email);

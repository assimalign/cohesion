using System;
using System.IO;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

// The SQL engine, named once. Its callback runs while the application is built, so configuration
// (appsettings.json, environment variables, --Database:DataPath=... or --Database:Endpoint=...
// arguments) has its final values here.
builder.AddSql("__RESOURCE_NAME__", sql =>
{
    sql.Options.RootPath = builder.Configuration["Database:DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data");
    sql.AddServer(server => server.Listen(new Uri(builder.Configuration["Database:Endpoint"] ?? "cohesion-db://localhost:5740")));

    // The engine's build creates the database when it does not exist and applies this schema
    // before the server accepts its first connection. Every member the table stores is declared:
    // Key, Column, Index and References each add the member they select as a column.
    sql.AddDatabase("customers", database => database.Schema(schema =>
    {
        schema.Table<Customer>("Customers", table =>
        {
            table.Key(customer => customer.Id);
            table.Column(customer => customer.Name);
            table.Column(customer => customer.Email);
            table.Index(customer => customer.Email);
        });
    }));
});

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

internal sealed record Customer(long Id, string Name, string Email);

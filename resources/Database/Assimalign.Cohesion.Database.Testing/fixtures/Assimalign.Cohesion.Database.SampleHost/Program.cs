using System;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

builder.AddSql("sample-sql", sql =>
{
    sql.Options.RootPath = builder.Configuration["Sample:DataPath"] ?? "data";
    sql.AddServer(server => server.Listen(new Uri("cohesion-db://127.0.0.1:5740")));

    // The engine's build provisions the database before application Build returns; the SDK reads
    // this declaration to emit obj/.../cohesion/database/sample.schema.json at build time.
    sql.AddDatabase("sample", database => database.Schema(schema =>
    {
        schema.Table<Order>("orders", table =>
        {
            table.Key(order => order.Id);
            table.Column(order => order.Item);
            table.Index(order => order.Item);
        });
    }));
});

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

/// <summary>
/// Exposes the compiler-generated top-level entry point to in-process test factories.
/// </summary>
public partial class Program;

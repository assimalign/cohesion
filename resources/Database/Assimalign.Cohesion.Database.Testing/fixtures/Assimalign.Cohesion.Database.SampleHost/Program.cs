using System;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.SampleHost;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Hosting;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

builder.AddSql("sample-sql", sql =>
{
    sql.Options.RootPath = Resource.Mounts.Data.Path
        ?? throw new InvalidOperationException("The Database data mount must have a materialized path.");
    sql.Options.Durability = Resource.Settings.DatabaseDurability.Get<StorageCommitDurability>();
    sql.AddServer(server => server.Listen(Resource.Endpoints.Db));

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

internal sealed record Order(long Id, string Item);

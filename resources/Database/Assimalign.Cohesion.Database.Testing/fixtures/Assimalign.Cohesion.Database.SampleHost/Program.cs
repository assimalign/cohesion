using System;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.SampleHost;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
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

ISqlSchema declaration = SqlSchema.Create("sample", database =>
{
    database.Table<Order>("orders", table =>
    {
        table.Key(order => order.Id);
        table.Column(order => order.Item);
        table.Index(order => order.Item);
    });
});

builder.AddDatabase(engine, "sample", SqlSchemaCompiler.Compile(declaration, EngineModel.Sql));

builder.AddSqlServer(engine, options => options.Listen(Resource.Endpoints.Db));

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

/// <summary>
/// Exposes the compiler-generated top-level entry point to in-process test factories.
/// </summary>
public partial class Program;

internal sealed record Order(long Id, string Item);

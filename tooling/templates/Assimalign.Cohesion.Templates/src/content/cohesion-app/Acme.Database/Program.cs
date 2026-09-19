using System;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Acme.Database;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

await using SqlDatabaseEngine engine = builder.AddSqlDatabase(options =>
{
    options.EngineName = "acme-sql";
    options.RootPath = Resource.Mounts.Data.Path
        ?? throw new InvalidOperationException("The database data mount must have a materialized path.");
});

SqlCompiledSchema schema = SqlSchema.Compile("customers", database =>
{
    database.Table<Customer>("Customers", table =>
    {
        table.Key(customer => customer.Id);
        table.Index(customer => customer.Email);
    });
    database.Principal(
        "acme-api",
        principal => principal.Grant(SqlPermission.ReadWrite, "Customers"));
});

builder.AddDatabase(engine, "customers", schema);

builder.AddSqlServer(engine, options => options.Listen(Resource.Endpoints.Db));

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

internal sealed record Customer(long Id, string Name, string Email);

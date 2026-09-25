using System;
using System.IO;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Hosting;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

builder.AddSql((_, options) =>
{
    options.EngineName = "__RESOURCE_NAME__";
    options.RootPath = Path.Combine(AppContext.BaseDirectory, "data");
    options.AddServer(engine => SqlDatabaseServer.Create(
        (SqlDatabaseEngine)engine, new SqlDatabaseServerOptions().Listen(new Uri("cohesion-db://localhost:5740"))));
});

SqlCompiledSchema schema = SqlSchema.Compile("customers", database =>
{
    database.Table<Customer>("Customers", table =>
    {
        table.Key(customer => customer.Id);
        table.Index(customer => customer.Email);
    });
    database.Principal(
        "__RESOURCE_NAME__-api",
        principal => principal.Grant(SqlPermission.ReadWrite, "Customers"));
});

builder.AddDatabase("__RESOURCE_NAME__", "customers", schema);

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

internal sealed record Customer(long Id, string Name, string Email);

using System;
using System.IO;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

await using SqlDatabaseEngine engine = builder.AddSqlDatabase(options =>
{
    options.EngineName = "__RESOURCE_NAME__";
    options.RootPath = Path.Combine(AppContext.BaseDirectory, "data");
});

builder.AddDatabase(engine, "customers", database =>
{
    database.Table<Customer>(table =>
    {
        table.Key(customer => customer.Id);
        table.Index(customer => customer.Email);
    });
    database.Principal(
        "__RESOURCE_NAME__-api",
        principal => principal.Grant(Permission.ReadWrite, "Customers"));
});

builder.AddSqlServer(engine, options => options.Listen(new Uri("cohesion-db://localhost:5740")));

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

internal sealed record Customer(long Id, string Name, string Email);

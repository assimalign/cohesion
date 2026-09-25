using Assimalign.Cohesion.Database.Hosting;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);
await using DatabaseApplication application = builder.Build();

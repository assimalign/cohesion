using Assimalign.Cohesion.Database.Hosting;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);
await using DatabaseApplication application = builder.Build();

/// <summary>Marks the fixture's top-level resource entry point.</summary>
public partial class Program
{
}

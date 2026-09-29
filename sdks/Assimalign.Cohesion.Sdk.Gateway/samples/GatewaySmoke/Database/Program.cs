using Assimalign.Cohesion.Database.Hosting;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);
builder.AddService(new GatewaySmoke.Database.ProcessMarker());
await using DatabaseApplication application = builder.Build();
await application.RunAsync();

/// <summary>Marks the sample's top-level resource entry point.</summary>
public partial class Program
{
}

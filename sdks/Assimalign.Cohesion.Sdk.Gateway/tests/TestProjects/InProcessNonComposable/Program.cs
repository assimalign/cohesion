using Assimalign.Cohesion.Web.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication application = builder.Build();
await application.RunAsync();

/// <summary>Marks the fixture's top-level resource entry point.</summary>
public partial class Program
{
}

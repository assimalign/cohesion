using System;
using Assimalign.Cohesion.Web.Hosting;

Uri database = global::Web.Resource.References.GatewaySmokeDatabase.Db.Url;
if (!database.IsLoopback)
{
    throw new InvalidOperationException(
        $"The in-process Database reference must be loopback, but was '{database}'.");
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddService(new GatewaySmoke.Web.ProcessMarker());
builder.AddHealthCheck("restart-smoke", GatewaySmoke.Web.ProcessMarker.CheckRestartHealth);
WebApplication application = builder.Build();
await application.RunAsync();

/// <summary>Marks the sample's top-level resource entry point.</summary>
public partial class Program
{
}

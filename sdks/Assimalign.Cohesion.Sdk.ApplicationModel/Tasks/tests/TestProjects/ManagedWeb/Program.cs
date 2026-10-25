using System;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Hosting.Management;

using ManagedWeb;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string origin = builder.Configuration["Cors:Origin"] ?? throw new InvalidOperationException("Startup CORS configuration is missing.");
CorsPolicyStore cors = CorsPolicyStore.Create(new CorsConfiguration
{
    DefaultPolicy = new CorsPolicyConfiguration { Origins = [origin] }
});
builder.Services.AddSingleton<IResourceStateHandler>(CorsResourceStateHandler.Create(cors));
await using WebApplication application = builder.Build();
if (!ResourceRuntime.TryGetControlPlane(application, out IResourceControlPlane? plane) ||
    plane.RuntimeManifest?.States.Count != 1 || plane.RuntimeManifest.InitialState.Count != 1 ||
    plane.RuntimeManifest.InitialState[0].Desired.Value.GetProperty("defaultPolicy").GetProperty("origins")[0].GetString() != origin)
{
    return 1;
}
Console.WriteLine("Runtime catalog captures effective startup configuration.");
return 0;

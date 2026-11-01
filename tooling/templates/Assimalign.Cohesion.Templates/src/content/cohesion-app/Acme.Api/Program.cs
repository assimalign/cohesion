using System.Text;

using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Hosting.ApplicationModel;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Acme.Gateway's model declares this application as the Web resource acme-api. Started by a gateway,
// it publishes itself through the platform the gateway names and the gateway can manage it; started on
// its own, no platform is named and this call registers nothing.
builder.Services.AddResource("acme-api", resource => resource.UseInProcess());

// Settings come from configuration: appsettings.json, environment variables, or the values a gateway
// passes when it deploys the resource.
string database = builder.Configuration["Database:Endpoint"] ?? "cohesion-db://localhost:5740";
string pageSize = builder.Configuration["Customers:PageSize"] ?? "25";

await using WebApplication application = builder.Build();

application.Use(async (context, next) =>
{
    if (context.Request.Path.Value != "/bindings")
    {
        await next.Invoke(context).ConfigureAwait(false);
        return;
    }

    context.Response.StatusCode = HttpStatusCode.Ok;
    byte[] payload = Encoding.UTF8.GetBytes($"resource=acme-api;database={database};pageSize={pageSize}");
    await context.Response.Body.WriteAsync(payload, context.RequestCancelled).ConfigureAwait(false);
});

await application.RunAsync();

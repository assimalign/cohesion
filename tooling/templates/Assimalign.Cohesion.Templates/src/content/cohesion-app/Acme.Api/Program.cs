using System.Text;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Settings come from configuration: appsettings.json or environment variables.
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

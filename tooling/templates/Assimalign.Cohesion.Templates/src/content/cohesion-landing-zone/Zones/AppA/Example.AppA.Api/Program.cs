using System;
using System.Text;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;
using Example.AppA.Api;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
await using WebApplication application = builder.Build();

Uri database = Resource.References.AppADatabase.Db.Url;
Uri configuration = Resource.References.PlatformConfigurationstore.Api.Url;
int pageSize = Resource.Settings.OrdersPageSize.Get<int>();

application.Use(async (context, next) =>
{
    if (context.Request.Path.Value != "/bindings")
    {
        await next.Invoke(context).ConfigureAwait(false);
        return;
    }

    context.Response.StatusCode = HttpStatusCode.Ok;
    byte[] payload = Encoding.UTF8.GetBytes(
        $"resource={Resource.Name};database={database};configuration={configuration};pageSize={pageSize}");
    await context.Response.Body.WriteAsync(payload, context.RequestCancelled).ConfigureAwait(false);
});

await application.RunAsync();

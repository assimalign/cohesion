using System;
using System.Text;

using Acme.Api;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
await using WebApplication application = builder.Build();

Uri database = Resource.References.AcmeDatabase.Db.Url;
int pageSize = Resource.Settings.CustomersPageSize.Get<int>();

application.Use(async (context, next) =>
{
    if (context.Request.Path.Value != "/bindings")
    {
        await next.Invoke(context).ConfigureAwait(false);
        return;
    }

    context.Response.StatusCode = HttpStatusCode.Ok;
    byte[] payload = Encoding.UTF8.GetBytes(
        $"resource={Resource.Name};database={database};pageSize={pageSize}");
    await context.Response.Body.WriteAsync(payload, context.RequestCancelled).ConfigureAwait(false);
});

await application.RunAsync();

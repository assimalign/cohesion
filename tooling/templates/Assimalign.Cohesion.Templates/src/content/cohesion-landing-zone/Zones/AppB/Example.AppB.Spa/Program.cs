using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;
using Example.AppB.Spa;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
await using WebApplication application = builder.Build();

byte[] index = File.ReadAllBytes(Path.Combine(
    ResourceRuntime.Current.ContentRootPath,
    "wwwroot",
    "index.html"));
byte[] configuration = Encoding.UTF8.GetBytes(
    $"{{\"references\":{{\"appbApi\":{{\"http\":\"{Resource.References.AppBApi.Http.Url}\"}}}}}}");

application.Use(async (context, next) =>
{
    byte[]? payload = context.Request.Path.Value switch
    {
        "/" => index,
        "/cohesion.config.json" => configuration,
        _ => null,
    };
    if (payload is null)
    {
        await next.Invoke(context).ConfigureAwait(false);
        return;
    }

    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync(payload, context.RequestCancelled).ConfigureAwait(false);
});

await application.RunAsync();

using System;
using System.IO;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
await using WebApplication application = builder.Build();
byte[] index = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));

application.Use(async (context, next) =>
{
    if (context.Request.Path.Value != "/")
    {
        await next.Invoke(context).ConfigureAwait(false);
        return;
    }

    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync(index, context.RequestCancelled).ConfigureAwait(false);
});

await application.RunAsync();

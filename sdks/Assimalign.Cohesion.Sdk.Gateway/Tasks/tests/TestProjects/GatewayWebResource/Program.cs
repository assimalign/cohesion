using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Hosting.ApplicationModel;

// An ordinary top-level Web program that makes itself a resource. Nothing in it knows that a gateway
// runs it in the gateway's own process: the gateway binds this entry point through the bridge the
// build generated.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddResource("smoke-api", resource => resource.UseInProcess());

WebApplication application = builder.Build();
application.Use(async (context, _) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync("smoke-api answered"u8.ToArray());
});

await application.RunAsync();

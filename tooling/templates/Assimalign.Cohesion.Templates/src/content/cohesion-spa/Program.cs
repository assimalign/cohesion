using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.StaticFiles;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddRouting();
await using WebApplication application = builder.Build();

// Serves wwwroot: index.html for "/", plus every asset beside it with ETags, ranges and
// precompressed .br/.gz siblings.
application.UseStaticFiles();
application.UseRouting();

// Client-side routes serve index.html; missing paths naming a file still answer 404.
application.MapFallbackToFile("index.html");

await application.RunAsync();

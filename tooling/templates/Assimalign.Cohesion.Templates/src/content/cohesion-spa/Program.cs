using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.StaticFiles;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
await using WebApplication application = builder.Build();

// Serves wwwroot: index.html for "/", plus every asset beside it with ETags, ranges and
// precompressed .br/.gz siblings.
application.UseStaticFiles();

await application.RunAsync();

using System;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authentication.Bearer;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Routing;

using EnabledWeb;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
ISqlClient orders = SqlClient.Create(new SqlClientOptions
{
    Settings = DatabaseConnectionSettings.For(
        Resource.References.InventoryDatabase.Db.Url,
        database: "orders",
        principal: Resource.Name),
    ConnectionFactory = Resource.References.InventoryDatabase.Db.ConnectionFactory(),
});
builder.Services.AddSingleton(orders);

// The IdentityHub reference and JwtBearerOptions.Authority from section 4.2.2 do not
// exist yet. The real bearer API accepts explicit issuer URLs, so this build-only
// acceptance fixture uses its own endpoint as the nearest available composition.
Uri authority = Resource.Endpoints.Http;
builder.AddAuthentication().AddJwtBearer(options =>
    options.ValidIssuers.Add(authority.AbsoluteUri));
builder.AddHealthCheck(
    "orders",
    _ => ValueTask.FromResult(HealthContribution.Healthy()));
builder.AddRouting();

int pageSize = Resource.Settings.OrdersPageSize.Get<int>();
WebApplication app = builder.Build();
app.UseRouting();
app.MapGet("/orders/{id}", async (long id, IHttpContext context) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync(
        Encoding.UTF8.GetBytes($"order:{id}"),
        context.RequestCancelled);
});
app.MapGet("/orders", async (IHttpContext context) =>
{
    context.Response.StatusCode = HttpStatusCode.Ok;
    await context.Response.Body.WriteAsync(
        Encoding.UTF8.GetBytes($"page-size:{pageSize}"),
        context.RequestCancelled);
});
await app.RunAsync();

/// <summary>Exposes the top-level entry marker to resource-program test factories.</summary>
public partial class Program
{
}

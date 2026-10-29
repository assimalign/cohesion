using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel.Tests;

/// <summary>
/// The <c>orders-api</c> resource's program, as its <c>Program.cs</c> would be: an ordinary Web
/// application that makes itself a resource and answers with its name. Nothing in it knows where it
/// runs.
/// </summary>
internal static class OrdersProgram
{
    public static async Task MainAsync(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.AddResource("orders-api");

        WebApplication application = builder.Build();
        application.Use(async (context, _) =>
        {
            context.Response.StatusCode = HttpStatusCode.Ok;
            await context.Response.Body.WriteAsync("orders"u8.ToArray());
        });

        await application.RunAsync();
    }
}

/// <summary>
/// The <c>storefront</c> resource's program: it reads the address of <c>orders-api</c> from its
/// configuration, where its <c>Orders:Url</c> setting arrives, and answers with what that returns.
/// </summary>
internal static class StorefrontProgram
{
    public static async Task MainAsync(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        string orders = builder.Configuration["Orders:Url"] ?? throw new InvalidOperationException("The Orders:Url setting did not arrive.");
        builder.AddResource("storefront");

        WebApplication application = builder.Build();
        using var client = new HttpClient { BaseAddress = new Uri(orders) };
        application.Use(async (context, _) =>
        {
            string answer = await client.GetStringAsync("/");
            context.Response.StatusCode = HttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"storefront -> {answer}"));
        });

        await application.RunAsync();
    }
}

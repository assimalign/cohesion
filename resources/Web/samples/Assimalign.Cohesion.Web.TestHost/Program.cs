using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;

namespace Assimalign.Cohesion.Web.Testing.TestHost;

/// <summary>Executable fixture driven through the public Web test factory.</summary>
public sealed class Program
{
    /// <summary>Runs one ambient Web resource invocation until its control plane stops it.</summary>
    /// <param name="args">Arguments forwarded by the test factory.</param>
    /// <returns>The resource host's complete lifetime.</returns>
    public static async Task Main(string[] args)
    {
        ResourceContext resource = ResourceRuntime.Current;
        string marker = resource.GetSetting("Test:Marker", fallback: "missing");
        string argument = args.Length == 0 ? "none" : args[0];
        string mount = Encoding.UTF8.GetString(resource.Mounts["fixture"].ReadAllBytes());
        string reference = resource.References["inventory-database:db"].ToEndpointString();
        string credential = Convert.ToBase64String(resource.BootstrapCredential.Span);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.AddHealthCheck(
            "ambient",
            _ => ValueTask.FromResult(marker.StartsWith("unhealthy", StringComparison.Ordinal)
                ? HealthContribution.Unhealthy(marker)
                : HealthContribution.Healthy(marker)));

        await using WebApplication application = builder.Build();
        application.Use(async (context, next) =>
        {
            if (context.Request.Path.Value == "/request-authorization")
            {
                context.Response.StatusCode = HttpStatusCode.Ok;
                string authorization = context.Request.Headers.GetValue(HttpHeaderKey.Authorization)
                    ?? "none";
                await context.Response.Body.WriteAsync(
                        Encoding.UTF8.GetBytes(authorization),
                        context.RequestCancelled)
                    .ConfigureAwait(false);
                return;
            }

            if (context.Request.Path.Value != "/ambient")
            {
                await next.Invoke(context).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = HttpStatusCode.Ok;
            byte[] payload = Encoding.UTF8.GetBytes(
                $"{resource.ResourceName}|{resource.EnvironmentName}|{marker}|{argument}|" +
                $"{mount}|{reference}|{credential}");
            await context.Response.Body.WriteAsync(payload, context.RequestCancelled)
                .ConfigureAwait(false);
        });

        await application.RunAsync().ConfigureAwait(false);
        if (string.Equals(marker, "unhealthy-then-throw", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Expected fixture failure after graceful stop.");
        }
    }
}

internal static class TestResourceRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        ResourceRuntime.RegisterEntry(typeof(Program).Assembly);
        ResourceRuntime.RegisterControlPlane(
            typeof(Program).Assembly,
            static () => ResourceControlPlane.Create());
    }
}

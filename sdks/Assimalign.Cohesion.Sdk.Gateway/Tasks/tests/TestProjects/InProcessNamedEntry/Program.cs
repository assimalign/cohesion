using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Hosting;

namespace InProcessNamedEntry;

/// <summary>Provides a non-default entry-point declaring type for PE metadata coverage.</summary>
public static class CustomEntry
{
    /// <summary>Builds and runs the named-entry fixture.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The fixture lifetime.</returns>
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        WebApplication application = builder.Build();
        await application.RunAsync();
    }
}

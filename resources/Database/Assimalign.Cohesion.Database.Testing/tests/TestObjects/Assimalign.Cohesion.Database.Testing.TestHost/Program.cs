using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Database.Testing.TestHost;

/// <summary>
/// Minimal executable fixture proving the test factory drives a resource entry point rather
/// than a test-only builder callback.
/// </summary>
public sealed class Program
{
    /// <summary>Runs the fixture until its Database control plane requests shutdown.</summary>
    /// <param name="args">The resource arguments.</param>
    /// <returns>A task that completes after graceful shutdown.</returns>
    public static async Task Main(string[] args)
    {
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);
        await using DatabaseApplication application = builder.Build();
        await application.RunAsync();
    }
}

internal static class TestResourceControlPlaneRegistration
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

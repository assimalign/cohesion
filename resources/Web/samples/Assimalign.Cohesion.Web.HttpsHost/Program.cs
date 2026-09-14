using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting;

namespace Assimalign.Cohesion.Web.HttpsHost;

/// <summary>Real Web.Hosting executable for gateway HTTPS acceptance tests.</summary>
public static class Program
{
    /// <summary>Runs the ambient HTTPS resource until its gateway stops it.</summary>
    /// <param name="args">Application arguments.</param>
    /// <returns>The complete host lifetime.</returns>
    public static async Task Main(string[] args)
    {
        await using WebApplication application = WebApplication.CreateBuilder(args).Build();
        await application.RunAsync().ConfigureAwait(false);
    }

    [ModuleInitializer]
    internal static void Register()
    {
        ResourceRuntime.RegisterEntry(typeof(Program).Assembly);
        ResourceRuntime.RegisterControlPlane(typeof(Program).Assembly, static () => ResourceControlPlane.Create());
    }
}

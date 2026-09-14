using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.LogSpace.Hosting;

namespace Assimalign.Cohesion.LogSpace.SinkHost;

/// <summary>Real LogSpace executable for gateway telemetry acceptance tests.</summary>
public static class Program
{
    /// <summary>Runs the ambient LogSpace sink until its gateway stops it.</summary>
    /// <param name="args">Application arguments.</param>
    /// <returns>The complete host lifetime.</returns>
    public static async Task Main(string[] args)
    {
        await using ILogSpaceApplication application = LogSpaceApplication.CreateBuilder(args).Build();
        await application.RunAsync().ConfigureAwait(false);
    }

    [ModuleInitializer]
    internal static void Register()
    {
        ResourceRuntime.RegisterEntry(typeof(Program).Assembly);
        ResourceRuntime.RegisterControlPlane(typeof(Program).Assembly, static () => ResourceControlPlane.Create());
    }
}

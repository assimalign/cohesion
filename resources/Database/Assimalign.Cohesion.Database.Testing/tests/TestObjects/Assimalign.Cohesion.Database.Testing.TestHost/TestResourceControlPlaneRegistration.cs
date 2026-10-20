using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Database.Testing.TestHost;

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

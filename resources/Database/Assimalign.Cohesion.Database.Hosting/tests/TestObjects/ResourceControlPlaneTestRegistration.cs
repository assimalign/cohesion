using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

internal static class ResourceControlPlaneTestRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        ResourceRuntime.RegisterControlPlane(
            typeof(ResourceControlPlaneTestRegistration).Assembly,
            static () => ResourceControlPlane.Create(["database.add-database", "database.add-principal"]));
    }
}

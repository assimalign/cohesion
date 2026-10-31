using System.Text;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Declarations and instance manifests of <c>Web/orders-api</c>.
/// </summary>
internal static class TestManifests
{
    public static readonly ActionName LogLevel = "log-level";

    public static readonly ActionName FlushCache = "flush-cache";

    public static ApplicationResourceManifest OrdersApi { get; } = new("Web", "orders-api", [], [], []);

    /// <summary>An instance with a stateful log-level control and an imperative flush.</summary>
    public static ApplicationInstanceManifest Instance()
    {
        return Instance(new ApplicationActionDescriptor(FlushCache, ApplicationActionKind.Write, false), new ApplicationActionDescriptor(LogLevel, ApplicationActionKind.ReadWrite, true));
    }

    public static ApplicationInstanceManifest Instance(params ApplicationActionDescriptor[] actions)
    {
        return new ApplicationInstanceManifest("Web", "orders-api", InstanceKey.New(), null, null, actions);
    }

    public static ApplicationActionSnapshot LogLevelSnapshot(string level)
    {
        return ApplicationActionSnapshot.Create(LogLevel, Encoding.UTF8.GetBytes($$"""{"Level":"{{level}}"}"""));
    }
}

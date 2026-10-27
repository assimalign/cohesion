using System.Text;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// Declarations and instance manifests of <c>Web/orders-api</c>.
/// </summary>
internal static class TestManifests
{
    public static readonly ResourceActionName LogLevel = "log-level";

    public static readonly ResourceActionName FlushCache = "flush-cache";

    public static ApplicationResourceManifest OrdersApi { get; } = new("Web", "orders-api", []);

    /// <summary>An instance with a stateful log-level control and an imperative flush.</summary>
    public static ResourceManifest Instance()
    {
        return Instance(new ResourceActionDescriptor(FlushCache, ResourceActionKind.Write, false), new ResourceActionDescriptor(LogLevel, ResourceActionKind.ReadWrite, true));
    }

    public static ResourceManifest Instance(params ResourceActionDescriptor[] actions)
    {
        return new ResourceManifest("Web", "orders-api", ResourceInstanceKey.New(), actions);
    }

    public static ResourceStateSnapshot LogLevelSnapshot(string level)
    {
        return ResourceStateSnapshot.Create(LogLevel, Encoding.UTF8.GetBytes($$"""{"Level":"{{level}}"}"""));
    }
}

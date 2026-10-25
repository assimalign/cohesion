using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

/// <summary>A control plane with one live section and one action, for the runtime-surface routes.</summary>
internal static class RuntimeSurfaceTestObjects
{
    public static ResourceControlPlane CreateControlPlane()
    {
        ResourceControlPlane controlPlane = ResourceControlPlane.Create();
        controlPlane.AddConfiguration(ResourceConfigurationSection.Create(
            "Store:Limits",
            RuntimeSurfaceJsonContext.Default.SurfaceLimits,
            ResourceConfigurationMutability.Live,
            new SurfaceLimits(),
            static _ => null));
        controlPlane.AddAction(ResourceAction.Create(
            "ping",
            RuntimeSurfaceJsonContext.Default.PingInput,
            RuntimeSurfaceJsonContext.Default.PingResult,
            ResourceActionTarget.AnyInstance,
            static (input, _) => ValueTask.FromResult(new PingResult { Text = input.Text })));
        return controlPlane;
    }

    public static JsonDocument ReadBody(Stream body) => JsonDocument.Parse(((MemoryStream)body).ToArray());
}

internal sealed class SurfaceLimits
{
    public int MaxItems { get; set; } = 10;
}

internal sealed class PingInput
{
    public string Text { get; set; } = string.Empty;
}

internal sealed class PingResult
{
    public string Text { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SurfaceLimits))]
[JsonSerializable(typeof(PingInput))]
[JsonSerializable(typeof(PingResult))]
internal sealed partial class RuntimeSurfaceJsonContext : JsonSerializerContext
{
}

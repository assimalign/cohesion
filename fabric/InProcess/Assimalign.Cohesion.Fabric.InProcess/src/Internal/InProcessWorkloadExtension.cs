using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

using Assimalign.Cohesion.Workloads;

namespace Assimalign.Cohesion.Fabric.InProcess.Internal;

/// <summary>
/// Reads <c>spec.platforms.inprocess</c> on a workload into <see cref="InProcessWorkloadSettings"/>.
/// </summary>
internal sealed class InProcessWorkloadExtension : FabricExtension<InProcessWorkloadSettings>
{
    private static readonly ApiVersion _version20261201 = new("2026-12-01");

    public override PlatformName Platform => InProcessPlatform.PlatformName;

    public override ResourceTypeName HostType => WorkloadResourceTypes.Workloads;

    public override IReadOnlyList<ApiVersion> ApiVersions { get; } = [_version20261201];

    public override InProcessWorkloadSettings Read(ApiVersion version, JsonElement block)
    {
        if (version != _version20261201)
        {
            throw new JsonException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ExtensionVersionUnsupported, version));
        }

        return block.Deserialize(InProcessJsonContext.Default.InProcessWorkloadSettings)
            ?? throw new JsonException(ErrorMessages.ExtensionBlockInvalid);
    }
}

using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.Fabric.Tests.TestObjects;

internal sealed class TestHub;

internal sealed class TestExtension : FabricExtension<TestHub>
{
    public TestExtension(string platform, string hostType)
    {
        Platform = new PlatformName(platform);
        HostType = new ResourceTypeName(hostType);
    }

    public override PlatformName Platform { get; }

    public override ResourceTypeName HostType { get; }

    public override IReadOnlyList<ApiVersion> ApiVersions { get; } = [new("2026-12-01")];

    public override TestHub Read(ApiVersion version, JsonElement block) => new();
}

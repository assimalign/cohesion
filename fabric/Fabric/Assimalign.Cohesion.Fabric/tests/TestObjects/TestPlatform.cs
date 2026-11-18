using System;

namespace Assimalign.Cohesion.Fabric.Tests.TestObjects;

internal sealed class TestPlatform : FabricPlatform
{
    private readonly Action<IFabricPlatformBuilder> _configure;
    private readonly PlatformName _name;

    public TestPlatform(Action<IFabricPlatformBuilder> configure, string? name = "test")
    {
        _configure = configure;
        _name = name is null ? default : new PlatformName(name);
    }

    public override PlatformName Name => _name;

    public override ApiVersion ContractVersion { get; } = new("2026-12-01");

    public override void Configure(IFabricPlatformBuilder platform) => _configure(platform);
}

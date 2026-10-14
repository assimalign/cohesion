namespace Assimalign.Cohesion.Connections.StreamLimits.Tests.TestObjects;

internal sealed class TestFeature : IMultiplexedConnectionStreamLimits
{
    public string Name => IMultiplexedConnectionStreamLimits.FeatureName;
    public long InboundBidirectionalStreamLimit => 17;
}

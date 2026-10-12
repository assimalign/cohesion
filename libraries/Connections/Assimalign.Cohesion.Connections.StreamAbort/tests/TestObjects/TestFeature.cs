namespace Assimalign.Cohesion.Connections.StreamAbort.Tests.TestObjects;

internal sealed class TestFeature : IMultiplexedStreamAbort
{
    public string Name => IMultiplexedStreamAbort.FeatureName;
    public void AbortRead(long errorCode) { }
    public void AbortWrite(long errorCode) { }
}

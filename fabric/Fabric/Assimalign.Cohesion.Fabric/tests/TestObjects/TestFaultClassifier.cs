namespace Assimalign.Cohesion.Fabric.Tests.TestObjects;

internal sealed class TestFaultClassifier : IFabricFaultClassifier
{
    public FabricFault Classify(PlatformDetail detail, ResourceUid resource)
        => new(FaultCode.PlatformError, FaultDisposition.Transient, detail.Reason, detail);
}

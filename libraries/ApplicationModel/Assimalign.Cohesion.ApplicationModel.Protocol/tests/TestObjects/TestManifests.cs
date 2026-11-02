using System;

namespace Assimalign.Cohesion.ApplicationModel.Protocol.Tests;

/// <summary>
/// The manifest an instance of <c>Web/orders-api</c> publishes, with one stateful action.
/// </summary>
internal static class TestManifests
{
    public static ApplicationInstanceManifest Instance(DeploymentId? deployment = null, Uri? controlPlaneEndpoint = null)
    {
        return new ApplicationInstanceManifest(
            "Web",
            "orders-api",
            InstanceKey.New(),
            deployment,
            controlPlaneEndpoint,
            [new ApplicationActionDescriptor("log-level", ApplicationActionKind.ReadWrite, IsStateful: true)]);
    }
}

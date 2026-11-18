namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// Maps a platform's reasons onto Cohesion's <see cref="FaultCode"/> catalog. Each platform
/// registers one through <see cref="IFabricPlatformBuilder.AddFaultClassifier"/>.
/// </summary>
public interface IFabricFaultClassifier
{
    /// <summary>
    /// Classifies one platform cause.
    /// </summary>
    /// <param name="detail">The platform's detail, kept verbatim in the result.</param>
    /// <param name="resource">The resource the cause belongs to, for the fault's message.</param>
    /// <returns>The fault. A reason the classifier does not know becomes <see cref="FaultCode.PlatformError"/>, with the detail intact.</returns>
    FabricFault Classify(PlatformDetail detail, ResourceUid resource);
}

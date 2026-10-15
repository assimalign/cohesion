namespace Assimalign.Cohesion.Web.Health.Internal;


/// <summary>
/// The default <see cref="IHealthFeature"/> attached to the request once a health endpoint
/// has produced its report.
/// </summary>
internal sealed class HealthFeature : IHealthFeature
{
    /// <summary>
    /// The stable feature slot name used when the feature is registered on the collection.
    /// </summary>
    public const string FeatureName = "Assimalign.Cohesion.Web.Health.Health";

    public HealthFeature(HealthReport report)
    {
        Report = report;
    }

    public string Name => FeatureName;

    public HealthReport Report { get; }
}

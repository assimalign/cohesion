namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Describes what applying a configuration section of a runtime surface changes.</summary>
public enum ResourceSurfaceScope
{
    /// <summary>The section configures each instance's own process (wire value <c>process</c>).</summary>
    Process,

    /// <summary>
    /// The section shapes data that every instance shares, such as a database's schema (wire value <c>backend</c>).
    /// Only code, claims and actions change it; the management API never overrides it.
    /// </summary>
    Backend,
}

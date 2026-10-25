namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Defines what applying a configuration section changes, and therefore which instances report it.</summary>
public enum ResourceConfigurationScope
{
    /// <summary>Each process applies the value to its own state, so every instance must report it applied.</summary>
    Process,

    /// <summary>
    /// The resource converges a backend it owns, such as its databases. The resource coordinates its own
    /// replicas, and the section accepts code-first values and claims but never management overrides.
    /// </summary>
    Backend,
}

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Defines when a deployment record is applied relative to the host's run.</summary>
public enum ResourceConfigurationPhase
{
    /// <summary>Before the host starts: every section applies, whatever its mutability.</summary>
    Startup,

    /// <summary>While the host runs: only live sections apply; other changed sections report a required restart.</summary>
    Running,
}

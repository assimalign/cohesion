using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.ApplicationModel;

/// <summary>
/// Options shaping how a <see cref="DatabaseResource"/> is declared to the gateway.
/// </summary>
public sealed class DatabaseResourceOptions
{
    /// <summary>
    /// Gets or sets the declared wire-protocol port. Zero (the default) lets the
    /// platform allocate a port; dependents discover it through the observed view, and
    /// the gateway injects the allocated port into the host at launch.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Gets or sets the path the persistent data volume is mounted at inside the
    /// realized resource. Surfaced to the host as the data-path environment variable.
    /// </summary>
    public string DataMountPath { get; set; } = "/data";

    /// <summary>
    /// Gets or sets the durability mode passed to the host (for example <c>full</c> or
    /// <c>relaxed</c>), or null to leave it to the host default.
    /// </summary>
    public string? Durability { get; set; }

    /// <summary>
    /// Gets the additional environment variables injected into the realized database
    /// process or container. Merged with the conventional database variables the
    /// resource derives from <see cref="Port"/>, <see cref="DataMountPath"/>, and
    /// <see cref="Durability"/>; values set here win on a name clash.
    /// </summary>
    public Dictionary<string, string> EnvironmentVariables { get; } = new();
}

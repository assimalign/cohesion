using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.ApplicationModel;

/// <summary>
/// Options shaping how a <see cref="DatabaseResource"/> is declared to the gateway.
/// </summary>
public sealed class DatabaseResourceOptions
{
    /// <summary>
    /// Gets or sets the declared wire-protocol port. Zero (the default) lets the
    /// platform allocate a port; dependents discover it through the observed view.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Gets or sets the path the persistent data volume is mounted at inside the
    /// realized resource.
    /// </summary>
    public string DataMountPath { get; set; } = "/data";

    /// <summary>
    /// Gets the environment variables injected into the realized database process
    /// or container.
    /// </summary>
    public Dictionary<string, string> EnvironmentVariables { get; } = new();
}

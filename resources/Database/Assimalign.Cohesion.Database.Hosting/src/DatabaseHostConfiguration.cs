using System;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>
/// The host configuration conventions the database host binds from its environment.
/// </summary>
/// <remarks>
/// These are the environment-variable conventions a gateway injects when it launches
/// the database host (the <c>Database.ApplicationModel</c> resource sets the same
/// names on its realized process). Binding them here — in the hosting module, the one
/// DI/Configuration seam — keeps the composition root free of ad-hoc environment
/// reads. The bound values shape how the composition root builds the engine (data
/// path, durability) and the listener (port); the host itself stays composition-only.
/// </remarks>
public sealed class DatabaseHostConfiguration
{
    /// <summary>
    /// The environment variable naming the directory the engine stores its data in.
    /// </summary>
    public const string DataPathVariable = "COHESION_DATABASE_DATA_PATH";

    /// <summary>
    /// The environment variable naming the port the wire-protocol endpoint listens on.
    /// </summary>
    public const string PortVariable = "COHESION_DATABASE_ENDPOINT_PORT";

    /// <summary>
    /// The environment variable naming the durability mode (for example <c>full</c> or
    /// <c>relaxed</c>) applied to the engine.
    /// </summary>
    public const string DurabilityVariable = "COHESION_DATABASE_DURABILITY";

    /// <summary>
    /// Gets or sets the directory the engine stores its data in, or null when unset
    /// (the engine falls back to its in-memory strategy).
    /// </summary>
    public string? DataPath { get; set; }

    /// <summary>
    /// Gets or sets the port the wire-protocol endpoint listens on, or null when unset
    /// (the platform allocates one).
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    /// Gets or sets the durability mode applied to the engine, or null when unset.
    /// </summary>
    public string? Durability { get; set; }

    /// <summary>
    /// Binds the configuration from the process environment using the conventional
    /// variable names.
    /// </summary>
    /// <returns>The bound configuration; properties are null when their variable is unset.</returns>
    /// <exception cref="FormatException">Thrown when the port variable is set to a non-integer or out-of-range value.</exception>
    public static DatabaseHostConfiguration FromEnvironment()
    {
        var configuration = new DatabaseHostConfiguration();

        string? dataPath = Environment.GetEnvironmentVariable(DataPathVariable);
        if (!string.IsNullOrWhiteSpace(dataPath))
        {
            configuration.DataPath = dataPath;
        }

        string? port = Environment.GetEnvironmentVariable(PortVariable);
        if (!string.IsNullOrWhiteSpace(port))
        {
            if (!int.TryParse(port, out int parsed) || parsed is < 0 or > ushort.MaxValue)
            {
                throw new FormatException($"Environment variable '{PortVariable}' must be a port in [0, {ushort.MaxValue}]; got '{port}'.");
            }

            configuration.Port = parsed;
        }

        string? durability = Environment.GetEnvironmentVariable(DurabilityVariable);
        if (!string.IsNullOrWhiteSpace(durability))
        {
            configuration.Durability = durability;
        }

        return configuration;
    }
}

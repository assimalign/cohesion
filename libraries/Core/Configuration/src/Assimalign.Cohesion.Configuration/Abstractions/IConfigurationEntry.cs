namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// 
/// </summary>
public interface IConfigurationEntry
{
    /// <summary>
    /// Gets the key this section occupies in its parent.
    /// </summary>
    ConfigKey Key { get; }
    /// <summary>
    /// The full path of the given <see cref="IConfigurationEntry" />
    /// </summary>
    ConfigPath Path { get; }
    /// <summary>
    /// The raw configuration value.
    /// </summary>
    object Value { get; set; }
}
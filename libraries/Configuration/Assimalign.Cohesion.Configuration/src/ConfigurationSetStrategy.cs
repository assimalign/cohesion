namespace Assimalign.Cohesion.Configuration;

public enum ConfigurationSetStrategy
{
    /// <summary>
    /// Only sets the <see cref="IConfigurationValue"/> or <see cref="IConfigurationSection"/>
    /// where a key already exists.
    /// </summary>
    ExistingOnly,

    /// <summary>
    /// Sets the <see cref="IConfigurationEntry"/>
    /// </summary>
    Distributed,
}

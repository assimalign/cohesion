namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// A section represents a composite in the configuration tree.
/// </summary>
public interface IConfigurationSection : IConfigurationEntry, IConfiguration
{
    /// <summary>
    /// The composite path
    /// </summary>
    KeyPath Path { get; }

    /// <summary>
    /// Gets the raw value by the underlying provider.
    /// </summary>
    /// <returns></returns>
    object? GetValue();
}
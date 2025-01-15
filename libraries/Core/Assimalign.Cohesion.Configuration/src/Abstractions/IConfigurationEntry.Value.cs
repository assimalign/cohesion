namespace Assimalign.Cohesion.Configuration;


/// <summary>
/// Represents a leaf entry in the configuration tree.
/// </summary>
public interface IConfigurationValue : IConfigurationEntry
{
    /// <summary>
    /// The composite path
    /// </summary>
    KeyPath Path { get; }

    /// <summary>
    /// The raw configuration value.
    /// </summary>
    object? Value { get; }
}

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// 
/// </summary>
public interface IConfigurationEntry
{
    /// <summary>
    /// Get the inner most child key.
    /// </summary>
    Key Key { get; }
    /// <summary>
    /// The raw configuration value.
    /// </summary>
    object? Value { get; }
}
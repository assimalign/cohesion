namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents a section of application configuration values.
/// </summary>
public interface IConfigurationSection : IConfigurationEntry
{
    /// <summary>
    /// 
    /// </summary>
    new IConfiguration Value { get; }
}
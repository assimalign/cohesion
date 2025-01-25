using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Configuration;


/// <summary>
/// Represents a leaf entry in the configuration tree.
/// </summary>
public interface IConfigurationValue : IConfigurationEntry
{
    /// <summary>
    /// The raw configuration value.
    /// </summary>
    string? Value { get; }
}

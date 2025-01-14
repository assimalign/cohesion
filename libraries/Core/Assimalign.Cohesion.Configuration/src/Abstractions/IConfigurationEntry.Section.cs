using System.Collections.Generic;

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
    /// Converts the the section into a raw value by the underlying provider.
    /// </summary>
    /// <returns></returns>
    object? ToValue();
}
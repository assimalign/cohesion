using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// A section represents a composite in the configuration tree.
/// </summary>
public interface IConfigurationSection : IConfigurationEntry, IConfiguration
{
    /// <summary>
    /// The number of entries in the section
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Adds a configuration entry the section.
    /// </summary>
    /// <param name="entry"></param>
    void Add(IConfigurationEntry entry);

    /// <summary>
    /// Removes an entry from the section
    /// </summary>
    /// <param name="entry"></param>
    void Remove(IConfigurationEntry entry);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    bool ContainsKey(Key key);
}
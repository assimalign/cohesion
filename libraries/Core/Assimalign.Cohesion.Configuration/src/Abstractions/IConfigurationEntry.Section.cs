using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// A section represents a composite in the configuration tree.
/// </summary>
public interface IConfigurationSection : IConfigurationEntry
{
    /// <summary>
    /// Returns either a <see cref="IConfigurationValue"/> or <see cref="IConfigurationSection"/>, if any.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IConfigurationEntry? GetEntry(Path path);

    /// <summary>
    /// Gets a collection of <see cref="IConfigurationEntry"/> that are children of this section."
    /// </summary>
    /// <returns></returns>
    IEnumerable<IConfigurationEntry> GetChildren();
}
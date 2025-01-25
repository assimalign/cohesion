using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// This interface acts as the base client for working with all the 
/// elements in the configuration tree.
/// </summary>
/// <remarks>
/// IConfiguration uses a composite pattern to manage configuration 
/// </remarks>
public interface IConfiguration : IEnumerable<IConfigurationEntry>
{
    /// <summary>
    /// Gets the <see cref="IConfigurationValue.Value"/> if any,
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    string? this[KeyPath path] { get; set; }

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
}
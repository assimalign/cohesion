using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// 
/// </summary>
public interface IConfiguration : IEnumerable<IConfigurationEntry>
{
    /// <summary>
    /// Gets or sets a configuration value.
    /// </summary>
    /// <param name="path">The configuration key.</param>
    /// <returns>The configuration value.</returns>
    object this[ConfigPath path] { get; set; }
}
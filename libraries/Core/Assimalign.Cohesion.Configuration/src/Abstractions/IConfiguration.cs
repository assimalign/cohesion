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
    /// <param name="key">The configuration key.</param>
    /// <returns>The configuration value.</returns>
    IConfigurationEntry this[Key key] { get; set; }
    /// <summary>
    /// Returns a section of the <see cref="IConfiguration"/> instance.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    IConfigurationSection GetSection(Key key);
    /// <summary>
    /// Gets a change token for the <see cref="IConfiguration"/> instance.
    /// </summary>
    /// <returns></returns>
    IConfigurationChangeToken GetChangeToken();
}
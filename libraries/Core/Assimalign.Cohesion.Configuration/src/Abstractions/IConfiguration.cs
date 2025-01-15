using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// <see cref="IConfiguration"/> represents a client for working with all the 
/// elements in the configuration tree.
/// </summary>
/// <remarks>
/// IConfiguration uses a composite pattern to manage configuration 
/// </remarks>
public interface IConfiguration : IEnumerable<IConfigurationEntry>
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    object? this[KeyPath path] { get; set; }

    /// <summary>
    /// Gets an <see cref="IConfigurationValue.Value"/> from the <see cref="IConfiguration"/> instance.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    IConfigurationValue GetValue(Key key);

    /// <summary>
    /// Gets all values in the <see cref="IConfiguration"/> instance.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IConfigurationValue> GetValues();

    /// <summary>
    /// Returns a section of the <see cref="IConfiguration"/> instance.
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    IConfigurationSection GetSection(Key key);

    /// <summary>
    /// Gets all sections in the <see cref="IConfiguration"/> instance.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IConfigurationSection> GetSections();

    /// <summary>
    /// Gets a change token for the <see cref="IConfiguration"/> instance.
    /// </summary>
    /// <returns></returns>
    IConfigurationChangeToken GetChangeToken();
}
using System.Threading;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
///  <see cref="IConfigurationEntry"/> represents the base for IConfigurationSection and IConfigurationValue which 
///  enables hierarchical configuration.
/// </summary>
public interface IConfigurationEntry
{
    /// <summary>
    /// The key of the entry.
    /// </summary>
    Key Key { get; }

    /// <summary>
    /// The full path of the entry.
    /// </summary>
    Path Path { get; }

    /// <summary>
    /// The name of the provider in which the entry belongs to.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Get a change token for entry
    /// </summary>
    /// <returns></returns>
    IChangeToken GetChangeToken();

    /// <summary>
    /// The provider in which the entry belongs to.
    /// </summary>
    //IConfigurationProvider Provider { get; }
}
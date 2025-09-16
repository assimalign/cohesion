using System.Threading;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents a single configuration entry.
/// </summary>
public interface IConfigurationEntry
{
    /// <summary>
    /// The entry key.
    /// </summary>
    Key Key { get; }

    /// <summary>
    /// The complete path of the entry.
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
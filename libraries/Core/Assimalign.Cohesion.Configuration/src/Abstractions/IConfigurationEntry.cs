using System;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// 
/// </summary>
public interface IConfigurationEntry 
{
    /// <summary>
    /// The entry key.
    /// </summary>
    Key Key { get; }

    /// <summary>
    /// Get a change token for entry
    /// </summary>
    /// <returns></returns>
    IConfigurationChangeToken GetChangeToken();
}
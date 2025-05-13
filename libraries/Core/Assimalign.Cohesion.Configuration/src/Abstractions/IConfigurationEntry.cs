using System;
using System.Threading;

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
    /// 
    /// </summary>
    Path Path { get; }

    /// <summary>
    /// The provider in which the entry belongs to.
    /// </summary>
    IConfigurationProvider Provider { get; }

    /// <summary>
    /// Get a change token for entry
    /// </summary>
    /// <returns></returns>
    IChangeToken GetChangeToken();
}
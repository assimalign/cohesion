using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// 
/// </summary>
public interface IConfigurationBuilderContext
{
    /// <summary>
    /// Gets a key/value collection that can be used to share data between the <see cref="IConfigurationBuilder"/>
    /// and the registration of <see cref="IConfigurationProvider"/>s.
    /// </summary>
    IDictionary<string, object> Properties { get; }

    /// <summary>
    /// A collection of registered providers.
    /// </summary>
    IEnumerable<IConfigurationProvider> Providers { get; }
}

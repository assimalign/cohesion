using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

public interface IConfigurationContext
{
    /// <summary>
    /// Gets a key/value collection that can be used to share data between the <see cref="IConfigurationBuilder"/>
    /// and the registered <see cref="IConfigurationProvider"/>s.
    /// </summary>
    IDictionary<string, object> Properties { get; }
    /// <summary>
    /// A collection of added providers.
    /// </summary>
    IEnumerable<IConfigurationProvider> Providers { get; }
}

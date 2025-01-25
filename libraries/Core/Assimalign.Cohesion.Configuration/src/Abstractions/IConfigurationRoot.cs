using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents the root of an <see cref="IConfiguration"/> hierarchy.
/// </summary>
public interface IConfigurationRoot : IConfiguration, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The <see cref="IConfigurationProvider"/>s for this configuration.
    /// </summary>
    IEnumerable<IConfigurationProvider> Providers { get; }
}
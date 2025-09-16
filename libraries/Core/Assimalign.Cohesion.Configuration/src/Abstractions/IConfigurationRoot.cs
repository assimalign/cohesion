using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents the root of an <see cref="IConfiguration"/> hierarchy.
/// </summary>
public interface IConfigurationRoot : IConfiguration, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Returns a collection of <see cref="IConfigurationProvider"/>.
    /// </summary>
    IEnumerable<IConfigurationProvider> Providers { get; }
}
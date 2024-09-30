﻿using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents the root of an <see cref="IConfiguration"/> hierarchy.
/// </summary>
public interface IConfigurationRoot : IConfiguration
{
    /// <summary>
    /// Force the configuration values to be reloaded from the underlying <see cref="IConfigurationProvider"/>s.
    /// </summary>
    void Reload();

    /// <summary>
    /// The <see cref="IConfigurationProvider"/>s for this configuration.
    /// </summary>
    IEnumerable<IConfigurationProvider> Providers { get; }
}

﻿using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents a type used to build application configuration.
/// </summary>
public interface IConfigurationBuilder
{
    /// <summary>
    /// Gets a key/value collection that can be used to share data between the <see cref="IConfigurationBuilder"/>
    /// and the registered <see cref="IConfigurationSource"/>s.
    /// </summary>
    IDictionary<string, object> Properties { get; }
    /// <summary>
    /// Adds a new configuration source.
    /// </summary>
    /// <param name="source">The configuration source to add.</param>
    /// <returns>The same <see cref="IConfigurationBuilder"/>.</returns>
    IConfigurationBuilder AddProvider(IConfigurationProvider provider);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="func"></param>
    /// <returns></returns>
    IConfigurationBuilder AddProvider(Func<IConfigurationProvider> func);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="provider"></param>
    /// <returns></returns>
    IConfigurationBuilder AddProvider(Func<IDictionary<string, object>, IConfigurationProvider> provider);
    /// <summary>
    /// Builds an <see cref="IConfiguration"/> with keys and values from the set of sources registered in
    /// <see cref="Sources"/>.
    /// </summary>
    /// <returns>An <see cref="IConfigurationRoot"/> with keys and values from the registered sources.</returns>
    IConfigurationRoot Build();
}
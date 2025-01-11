using System;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents a type used to build application configuration.
/// </summary>
public interface IConfigurationBuilder
{
    /// <summary>
    /// Adds a configuration provider to be built.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns>The same instance of <see cref="IConfigurationBuilder"/>.</returns>
    IConfigurationBuilder AddProvider(Func<IConfigurationContext, IConfigurationProvider> configure);
    /// <summary>
    /// Builds an <see cref="IConfiguration"/> with keys and values from the set of sources registered in
    /// <see cref="Sources"/>.
    /// </summary>
    /// <returns>An <see cref="IConfigurationRoot"/> with keys and values from the registered sources.</returns>
    IConfigurationRoot Build();
}
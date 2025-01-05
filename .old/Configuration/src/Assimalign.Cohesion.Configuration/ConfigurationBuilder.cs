using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Used to build key/value based configuration settings for use in an application.
/// </summary>
public class ConfigurationBuilder : IConfigurationBuilder
{
    private readonly IList<IConfigurationSource> sources;

    public ConfigurationBuilder()
    {
        this.sources = new List<IConfigurationSource>();
    }

    /// <summary>
    /// Returns the sources used to obtain configuration values.
    /// </summary>
    public IEnumerable<IConfigurationSource> Sources => this.sources;

    /// <summary>
    /// Gets a key/value collection that can be used to share data between the <see cref="IConfigurationBuilder"/>
    /// and the registered <see cref="IConfigurationProvider"/>s.
    /// </summary>
    public IDictionary<string, object> Properties { get; } = new Dictionary<string, object>();

    /// <summary>
    /// Adds a new configuration source.
    /// </summary>
    /// <param name="source">The configuration source to add.</param>
    /// <returns>The same <see cref="IConfigurationBuilder"/>.</returns>
    public IConfigurationBuilder Add(IConfigurationSource source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        sources.Add(source);
        return this;
    }

    /// <summary>
    /// Builds an <see cref="IConfiguration"/> with keys and values from the set of providers registered in
    /// <see cref="Sources"/>.
    /// </summary>
    /// <returns>An <see cref="IConfigurationRoot"/> with keys and values from the registered providers.</returns>
    public IConfigurationRoot Build()
    {
        var providers = new List<IConfigurationProvider>();
        foreach (IConfigurationSource source in Sources)
        {
            IConfigurationProvider provider = source.Build(this);
            providers.Add(provider);
        }
        return new ConfigurationRoot(providers);
    }
}

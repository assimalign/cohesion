using System;

using Assimalign.Cohesion.Configuration;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>Provides final hosting infrastructure to an owned engine factory.</summary>
public sealed class DatabaseApplicationBuildContext
{
    internal DatabaseApplicationBuildContext(IConfiguration configuration, IServiceProvider services)
    {
        Configuration = configuration;
        Services = services;
    }

    /// <summary>Gets the application's loaded configuration.</summary>
    public IConfiguration Configuration { get; }

    /// <summary>Gets the application provider; resolution does not transfer disposal ownership.</summary>
    public IServiceProvider Services { get; }
}

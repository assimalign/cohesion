using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Database.Hosting;

/// <summary>
/// Provides the application's built host-level pieces to an owned engine factory
/// (<see cref="DatabaseApplicationBuilder.AddEngine(string, System.Func{DatabaseApplicationBuildContext, DatabaseEngine})"/>):
/// the built counterparts of the builder's <see cref="DatabaseApplicationBuilder.Environment"/>,
/// <see cref="DatabaseApplicationBuilder.Configuration"/>, <see cref="DatabaseApplicationBuilder.Services"/>
/// and <see cref="DatabaseApplicationBuilder.Logging"/>.
/// </summary>
/// <remarks>
/// Engine factories run at Build, after the service container closed, so the context carries what
/// Build made of the builder's pieces, not the builders: the same environment and configuration
/// instances, the one service provider and the one logger factory. The application owns all of
/// them; a factory borrows them, and resolving a service does not transfer its ownership.
/// </remarks>
public sealed class DatabaseApplicationBuildContext
{
    internal DatabaseApplicationBuildContext(HostEnvironment environment, ConfigurationManager configuration, ServiceProvider services, LoggerFactory loggerFactory)
    {
        Environment = environment;
        Configuration = configuration;
        Services = services;
        LoggerFactory = loggerFactory;
    }

    /// <summary>Gets the application's host environment, the builder's <see cref="DatabaseApplicationBuilder.Environment"/>.</summary>
    public HostEnvironment Environment { get; }

    /// <summary>
    /// Gets the application's loaded configuration, the builder's
    /// <see cref="DatabaseApplicationBuilder.Configuration"/>; a factory reads it once, at Build.
    /// </summary>
    public ConfigurationManager Configuration { get; }

    /// <summary>
    /// Gets the application's service provider, built once from the builder's
    /// <see cref="DatabaseApplicationBuilder.Services"/>; resolution does not transfer disposal ownership.
    /// </summary>
    public ServiceProvider Services { get; }

    /// <summary>
    /// Gets the application's logger factory, built once from the builder's
    /// <see cref="DatabaseApplicationBuilder.Logging"/>.
    /// </summary>
    public LoggerFactory LoggerFactory { get; }
}

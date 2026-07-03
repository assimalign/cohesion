using System;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;

/// <summary>
/// The standalone hosting application for the configuration store resource. Composes the resource's
/// units of work as hosted services, each selecting its execution model per the
/// Assimalign.Cohesion.Hosting per-service execution menu (see docs/DESIGN.md).
/// </summary>
public sealed class ConfigurationStoreApplication : Host<ConfigurationStoreApplicationContext>
{
    private readonly ConfigurationStoreApplicationContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationStoreApplication"/> class.
    /// </summary>
    /// <param name="options">The application options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public ConfigurationStoreApplication(ConfigurationStoreApplicationOptions options) : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _context = new ConfigurationStoreApplicationContext(options, new IHostService[]
        {
            new ConfigurationEndpointService(),
        });
    }

    /// <summary>
    /// Gets the application context.
    /// </summary>
    public override ConfigurationStoreApplicationContext Context => _context;
}
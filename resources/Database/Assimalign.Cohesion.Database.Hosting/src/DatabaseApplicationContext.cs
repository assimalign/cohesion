using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.Database.Hosting;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// The host context for <see cref="DatabaseApplication"/> — the concrete
/// <see cref="IDatabaseApplicationContext"/>: the servers the application runs and
/// the engines registered without a server.
/// </summary>
/// <remarks>
/// The context wraps the live option lists, so a deferred server factory running at
/// build time (<see cref="IDatabaseApplicationBuilder.AddServer(System.Func{IDatabaseApplicationContext, IDatabaseServer})"/>)
/// observes every registration made before it — including servers produced by
/// earlier factories.
/// </remarks>
public sealed class DatabaseApplicationContext : HostContext, IDatabaseApplicationContext
{
    private readonly IHostEnvironment _environment;
    private readonly ReadOnlyCollection<IDatabaseEngine> _engines;
    private readonly ReadOnlyCollection<IDatabaseServer> _servers;
    private IReadOnlyList<IHostService> _hostedServices = [];

    internal DatabaseApplicationContext(DatabaseApplicationOptions options)
    {
        _environment = new HostEnvironment(options.Environment ?? "production");
        _engines = new ReadOnlyCollection<IDatabaseEngine>(options.Engines);
        _servers = new ReadOnlyCollection<IDatabaseServer>(options.Servers);
    }

    /// <summary>
    /// Gets the host environment information.
    /// </summary>
    public override IHostEnvironment Environment => _environment;

    /// <summary>
    /// Gets the hosted services composed by the application.
    /// </summary>
    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    /// <inheritdoc />
    public IReadOnlyList<IDatabaseEngine> Engines => _engines;

    /// <inheritdoc />
    public IReadOnlyList<IDatabaseServer> Servers => _servers;

    /// <summary>
    /// Binds the composed host services once <see cref="DatabaseApplication"/> has
    /// built them (the context itself is created ahead of the application so
    /// deferred server factories can receive it).
    /// </summary>
    internal void SetHostedServices(IReadOnlyList<IHostService> hostedServices)
        => _hostedServices = hostedServices;
}

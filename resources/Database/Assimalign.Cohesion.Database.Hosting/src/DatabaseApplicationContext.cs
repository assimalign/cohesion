using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Hosting;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// The host context for <see cref="DatabaseApplication"/>.
/// </summary>
public sealed class DatabaseApplicationContext : HostContext
{
    private readonly IHostEnvironment _environment;
    private readonly IReadOnlyList<IHostService> _hostedServices;

    internal DatabaseApplicationContext(DatabaseApplicationOptions options, IReadOnlyList<IHostService> hostedServices)
    {
        _environment = new HostEnvironment(options.Environment ?? "production");
        _hostedServices = hostedServices;
        Engines = new List<IDatabaseEngine>(options.Engines);
    }

    /// <summary>
    /// Gets the host environment information.
    /// </summary>
    public override IHostEnvironment Environment => _environment;

    /// <summary>
    /// Gets the hosted services composed by the application.
    /// </summary>
    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    /// <summary>
    /// Gets the engines this host serves.
    /// </summary>
    public IReadOnlyList<IDatabaseEngine> Engines { get; }
}

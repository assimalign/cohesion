using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Hosting;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// Options for <see cref="DatabaseApplication"/>: the wire-protocol servers the
/// application runs, the engines it holds as server-less embedded registrations,
/// and any additional host services.
/// </summary>
/// <remarks>
/// The hosting module is composition-only: it wraps each registered
/// <see cref="IDatabaseServer"/> in an internal endpoint host service, registered
/// after every other service so the servers start last and drain first. Engines
/// have no lifecycle for the host to drive — they are data machines, operational
/// from creation — so <see cref="Engines"/> is purely the application context's
/// observational registry of server-less engines. Per-model servers are composed by
/// the model packages (for example <c>SqlDatabaseServer</c> via the
/// <c>AddSqlServer</c> builder verb in <c>Assimalign.Cohesion.Database.Sql</c>) or
/// directly by the composition root, and assigned here.
/// </remarks>
public sealed class DatabaseApplicationOptions : HostOptions<DatabaseApplicationContext>
{
    /// <summary>
    /// Gets the engines this application holds as server-less, embedded
    /// registrations (exposed through
    /// <see cref="IDatabaseApplicationContext.Engines"/>). The composition root
    /// that created an engine owns its disposal; the application never starts,
    /// stops, or disposes engines.
    /// </summary>
    public IList<IDatabaseEngine> Engines { get; } = new List<IDatabaseEngine>();

    /// <summary>
    /// Gets the wire-protocol servers the application runs — one per model it
    /// serves. Each is wrapped in an internal endpoint host service registered
    /// last, so servers start last and drain first on stop.
    /// </summary>
    public IList<IDatabaseServer> Servers { get; } = new List<IDatabaseServer>();

    /// <summary>
    /// Gets the additional host services composed ahead of the servers. They start
    /// before the servers and stop after the servers have drained.
    /// </summary>
    public IList<IHostService> Services { get; } = new List<IHostService>();
}

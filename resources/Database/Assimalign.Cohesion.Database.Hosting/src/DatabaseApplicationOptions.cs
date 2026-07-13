using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Hosting;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// Options for <see cref="DatabaseApplication"/>: the engines the host serves, the
/// wire-protocol server, additional host services composed on top of the durability
/// worker slots, and toggles for the durability services.
/// </summary>
/// <remarks>
/// The hosting module owns the database server runtime (folded in from the former
/// <c>Database.Server</c> project), so the host composes the endpoint directly:
/// build the server with <see cref="DatabaseServer.Create"/> and assign it to
/// <see cref="Server"/> — <see cref="DatabaseApplication"/> wraps it in the internal
/// endpoint host service and registers it last, so it starts last and drains first.
/// </remarks>
public sealed class DatabaseApplicationOptions : HostOptions<DatabaseApplicationContext>
{
    /// <summary>
    /// Gets the engines this host serves. The application drives each engine's
    /// lifecycle through the root contract (<see cref="IDatabaseEngine.StartAsync"/>/
    /// <see cref="IDatabaseEngine.StopAsync"/>): engines start before every other
    /// composed service and stop last, after the endpoint has drained. An engine the
    /// composition root already started (for example to seed databases) is served
    /// as-is — engine start is idempotent.
    /// </summary>
    public IList<IDatabaseEngine> Engines { get; } = new List<IDatabaseEngine>();

    /// <summary>
    /// Gets or sets the wire-protocol server the host runs as its endpoint, or null
    /// when the host serves no network endpoint. The application wraps the server in
    /// its internal endpoint host service, registered last — the endpoint starts
    /// last and drains first on stop.
    /// </summary>
    public IDatabaseServer? Server { get; set; }

    /// <summary>
    /// Gets the additional host services composed on top of the worker slots and
    /// ahead of the endpoint. They start after the worker services and stop before
    /// them.
    /// </summary>
    public IList<IHostService> Services { get; } = new List<IHostService>();

    /// <summary>
    /// Gets the mapping of engine-owned background workers onto the execution menu:
    /// per worker kind, whether the host claims it and which execution model drives
    /// it. The host never owns the work — disabling a slot hands the loop back to
    /// the engine's own scheduler (see <see cref="DatabaseWorkerSlotOptions"/>).
    /// </summary>
    public DatabaseWorkerMappingOptions Workers { get; } = new();
}

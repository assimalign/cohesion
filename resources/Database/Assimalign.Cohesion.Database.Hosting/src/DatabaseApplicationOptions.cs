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
    /// Gets the engines this host serves. Engines are composed and started by the
    /// composition root and exposed here for the host context; engine start/stop is
    /// not yet part of the <see cref="IDatabaseEngine"/> contract (see docs/DESIGN.md).
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
    /// Gets the additional host services composed on top of the durability worker
    /// slots and ahead of the endpoint. They start after the durability services and
    /// stop before them.
    /// </summary>
    public IList<IHostService> Services { get; } = new List<IHostService>();

    /// <summary>
    /// Gets or sets a value indicating whether the write-ahead flush worker slot is
    /// composed. Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnableWriteAheadFlushService { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the page-writer worker slot is
    /// composed. Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnablePageWriterService { get; set; } = true;
}

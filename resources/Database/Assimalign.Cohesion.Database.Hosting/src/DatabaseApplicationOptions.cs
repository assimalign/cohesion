using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Hosting;

using Assimalign.Cohesion.Hosting;

/// <summary>
/// Options for <see cref="DatabaseApplication"/>: the engines the host serves, the
/// endpoint (and other) host services composed on top of the durability worker slots,
/// and toggles for the durability services.
/// </summary>
/// <remarks>
/// The host composes over the area root abstractions and non-area hosting
/// infrastructure only (the resource hosting-isolation rule, COHRES002). It therefore
/// cannot name the wire-protocol server directly; the composition root builds the
/// server from <c>Database.Server</c> and adds its endpoint host service
/// (<c>DatabaseServer.CreateHostService</c>) to <see cref="Services"/>.
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
    /// Gets the host services composed on top of the durability worker slots —
    /// typically the wire-protocol endpoint (<c>DatabaseServer.CreateHostService</c>).
    /// They start after the durability services and stop before them, so connections
    /// drain ahead of durability shutdown.
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

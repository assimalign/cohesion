using System.Collections.Generic;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The composed state of a database application: the wire-protocol servers it runs
/// and the engines registered without a server, exposed as one navigable surface.
/// </summary>
/// <remarks>
/// This is the Database instance of the cross-area context pattern
/// (<c>IWebApplicationContext</c> in the Web area): the application exposes its
/// composition through a context rather than as loose members, and deferred
/// composition callbacks on <see cref="IDatabaseApplicationBuilder"/> receive the
/// context so they can compose against the registered state. <see cref="Servers"/>
/// is plural because servers are per-model — one application may front a SQL engine
/// and a Documents engine through two servers. <see cref="Engines"/> carries the
/// server-less registrations (embedded, in-process data machines the application
/// merely holds); an engine fronted by a server is reachable through that server's
/// <see cref="IDatabaseServerContext.Engine"/>. The context is observational — it
/// navigates the composition; lifecycle stays on <see cref="IDatabaseApplication"/>
/// and, per server, on <see cref="IDatabaseServer"/>.
/// </remarks>
public interface IDatabaseApplicationContext
{
    /// <summary>
    /// Gets the database engines registered on the application without a server
    /// (embedded, in-process registrations), in registration order.
    /// </summary>
    IReadOnlyList<IDatabaseEngine> Engines { get; }

    /// <summary>
    /// Gets the wire-protocol servers the application runs, in registration order.
    /// Empty when the application is composed without an endpoint (a fully
    /// embedded composition).
    /// </summary>
    IReadOnlyList<IDatabaseServer> Servers { get; }
}

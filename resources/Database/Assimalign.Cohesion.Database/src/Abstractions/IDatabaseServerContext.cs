using System.Collections.Generic;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The composed state of a database server: the engine it fronts and the sessions
/// currently active on it.
/// </summary>
/// <remarks>
/// A server fronts exactly <b>one</b> engine — servers are per-model, so an
/// application that serves several models composes several servers
/// (<see cref="IDatabaseApplicationContext.Servers"/>), each with its own context.
/// The context is observational: it navigates the composition; lifecycle stays on
/// <see cref="IDatabaseServer"/>.
/// </remarks>
public interface IDatabaseServerContext
{
    /// <summary>
    /// Gets the database engine this server fronts.
    /// </summary>
    IDatabaseEngine Engine { get; }

    /// <summary>
    /// Gets the sessions currently active on this server.
    /// </summary>
    IReadOnlyCollection<IDatabaseServerSession> Sessions { get; }
}

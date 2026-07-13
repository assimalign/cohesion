using System.Collections.Generic;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The composed state of a database application: the engines it serves and the
/// wire-protocol endpoint fronting them, exposed as one navigable surface.
/// </summary>
/// <remarks>
/// This is the Database instance of the cross-area context pattern
/// (<c>IWebApplicationContext</c> in the Web area): the application exposes its
/// composition through a context rather than as loose members, and deferred
/// composition callbacks on <see cref="IDatabaseApplicationBuilder"/> receive the
/// context so they can compose against the final registered state. The context is
/// observational — it navigates the composition; it does not drive lifecycle
/// (that is <see cref="IDatabaseApplication"/> and, per engine,
/// <see cref="IDatabaseEngineLifecycle"/>).
/// </remarks>
public interface IDatabaseApplicationContext
{
    /// <summary>
    /// Gets the database engines the application serves, in registration order.
    /// </summary>
    IReadOnlyList<IDatabaseEngine> Engines { get; }

    /// <summary>
    /// Gets the wire-protocol server composed as the application's endpoint, or
    /// <see langword="null"/> when the application is composed without one
    /// (an embedded, in-process composition).
    /// </summary>
    IDatabaseServer? Server { get; }
}

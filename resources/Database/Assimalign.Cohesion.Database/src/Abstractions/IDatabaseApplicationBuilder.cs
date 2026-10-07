using System;

namespace Assimalign.Cohesion.Database;

/// <summary>Collects dependency-free engine intent for one database application.</summary>
/// <remarks>
/// Model verbs defer construction until Build. Servers and workers belong to engine builders. One
/// of the five interfaces the Database area keeps (the O34 composition seam, <c>database-area.md</c>);
/// its engine is the root base <see cref="DatabaseEngine"/> (concrete-types plan, phase 6, #1262).
/// </remarks>
public interface IDatabaseApplicationBuilder
{
    /// <summary>Registers a borrowed engine; its caller remains the disposal owner.</summary>
    /// <param name="engine">The existing engine.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Registration is closed because Build has begun, or another engine of the application has the same name.
    /// </exception>
    IDatabaseApplicationBuilder AddEngine(DatabaseEngine engine);

    /// <summary>Defers owned engine construction until the application's first Build attempt.</summary>
    /// <param name="configure">A factory receiving the engines already constructed in registration order.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Registration is closed because Build has begun.</exception>
    /// <remarks>
    /// The factory transfers ownership of a fresh engine. It cleans up allocations if it throws before returning.
    /// Its product's name is checked against the other engines at Build.
    /// </remarks>
    IDatabaseApplicationBuilder AddEngine(Func<IDatabaseApplicationContext, DatabaseEngine> configure);

    /// <summary>Consumes this builder and publishes a complete, disposable application.</summary>
    /// <returns>The application with live engines and listeners awaiting Start.</returns>
    /// <exception cref="InvalidOperationException">Build was already attempted or composition is invalid.</exception>
    IDatabaseApplication Build();
}

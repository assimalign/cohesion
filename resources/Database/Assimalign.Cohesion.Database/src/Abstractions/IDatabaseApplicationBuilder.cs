using System;
using System.Threading;

namespace Assimalign.Cohesion.Database;

/// <summary>Collects dependency-free engine intent for one database application.</summary>
/// <remarks>
/// Model verbs defer construction until Build, through the named <see cref="AddEngine(string, Func{IDatabaseApplicationContext, DatabaseEngine})"/>,
/// so the engine name is reserved when the verb is called. Servers, workers and declared databases
/// belong to engine builders. One of the five interfaces the Database area keeps (the O34
/// composition seam, <c>database-area.md</c>); its engine is the root base
/// <see cref="DatabaseEngine"/> (concrete-types plan, phase 6, #1262).
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

    /// <summary>
    /// Reserves an engine name now and defers the owned engine's construction until the
    /// application's first Build attempt.
    /// </summary>
    /// <param name="name">The engine name, which the factory's product must carry (ordinal).</param>
    /// <param name="factory">A factory receiving the engines already constructed in registration order.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <exception cref="InvalidOperationException">
    /// Registration is closed because Build has begun, or another engine of the application has the same name.
    /// </exception>
    /// <remarks>
    /// The name is reserved at this call, before any factory runs (owner decision 52 of 2026-10-09):
    /// a model's factory can open, recover and provision databases, so a duplicate must be refused
    /// before a second engine touches any file. The factory transfers ownership of a fresh engine and
    /// cleans up its allocations if it throws before returning. Build refuses a product whose name is
    /// not <paramref name="name"/>.
    /// </remarks>
    IDatabaseApplicationBuilder AddEngine(string name, Func<IDatabaseApplicationContext, DatabaseEngine> factory);

    /// <summary>Consumes this builder and publishes a complete, disposable application.</summary>
    /// <param name="cancellationToken">Cancels construction, recovery and inline provisioning.</param>
    /// <returns>The application with live engines and listeners awaiting Start.</returns>
    /// <exception cref="InvalidOperationException">Build was already attempted or composition is invalid.</exception>
    /// <exception cref="OperationCanceledException">Construction was canceled and owned products were disposed.</exception>
    /// <remarks>
    /// Build runs each owned engine factory synchronously, in registration order. A model's factory
    /// can do I/O here: a SQL engine's opens, recovers and provisions the databases its builder
    /// declares, with no timeout, observing this token through the application context (49a).
    /// Data-scaling SQL steps run on the engine's provisioning worker. A factory's
    /// exception propagates after the products built before it are disposed.
    /// </remarks>
    IDatabaseApplication Build(CancellationToken cancellationToken = default);
}

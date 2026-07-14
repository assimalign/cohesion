using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The composition surface for a database application. Model packages extend this
/// builder with registration verbs (for example <c>AddSqlDatabase(...)</c> and
/// <c>AddSqlServer(...)</c> in <c>Assimalign.Cohesion.Database.Sql</c>) so each
/// model registers its own engine and server against the area root's abstractions
/// without knowing the hosting layer.
/// </summary>
/// <remarks>
/// This is the Database instance of the cross-area builder pattern
/// (<c>IWebApplicationBuilder</c> in the Web area): the interface lives in the area
/// root, the implementation and creation entry point live in the hosting module
/// (<c>DatabaseApplication.CreateBuilder()</c>), and feature/model verbs ship with
/// their own package as <c>extension(IDatabaseApplicationBuilder)</c> members.
/// Registration is dependency-free by design — no service container, no
/// configuration binding — so any composition surface that implements this
/// interface can host a model. Servers are per-model, so <em>multiple</em> server
/// registrations are allowed — one per model the application serves.
/// </remarks>
public interface IDatabaseApplicationBuilder
{
    /// <summary>
    /// Gets the engines registered so far, in registration order.
    /// </summary>
    IReadOnlyList<IDatabaseEngine> Engines { get; }

    /// <summary>
    /// Registers a database engine the built application will hold as a
    /// server-less, embedded registration. An engine fronted by a server does not
    /// need separate registration — the server carries it.
    /// </summary>
    /// <param name="engine">The engine to register.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is <see langword="null"/>.</exception>
    IDatabaseApplicationBuilder AddEngine(IDatabaseEngine engine);

    /// <summary>
    /// Registers a pre-built wire-protocol server on the application. May be called
    /// multiple times — servers are per-model.
    /// </summary>
    /// <param name="server">The server to run.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    IDatabaseApplicationBuilder AddServer(IDatabaseServer server);

    /// <summary>
    /// Registers a deferred wire-protocol server factory. The factory runs at
    /// <see cref="Build"/> time and receives the application context, so it can
    /// compose against the final registered state (engines, and any servers
    /// registered ahead of it) regardless of verb ordering. Mirrors the Web area's
    /// <c>AddServer(Func&lt;IWebApplicationContext, IWebApplicationServer&gt;)</c>.
    /// </summary>
    /// <param name="configure">The factory that creates the server from the application context.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    IDatabaseApplicationBuilder AddServer(Func<IDatabaseApplicationContext, IDatabaseServer> configure);

    /// <summary>
    /// Builds the database application from the registered engines and servers.
    /// </summary>
    /// <returns>The composed application, ready to start.</returns>
    /// <exception cref="InvalidOperationException">The application has already been built.</exception>
    IDatabaseApplication Build();
}

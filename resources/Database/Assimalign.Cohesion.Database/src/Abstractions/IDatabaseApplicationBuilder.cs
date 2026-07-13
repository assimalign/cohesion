using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The composition surface for a database application. Model packages extend this
/// builder with registration verbs (for example <c>AddSqlDatabase(...)</c> in
/// <c>Assimalign.Cohesion.Database.Sql</c>) so each model registers its own engine
/// against the area root's abstractions without knowing the hosting layer.
/// </summary>
/// <remarks>
/// This is the Database instance of the cross-area builder pattern
/// (<c>IWebApplicationBuilder</c> in the Web area): the interface lives in the area
/// root, the implementation and creation entry point live in the hosting module
/// (<c>DatabaseApplication.CreateBuilder()</c>), and feature/model verbs ship with
/// their own package as <c>extension(IDatabaseApplicationBuilder)</c> members.
/// Registration is dependency-free by design — no service container, no
/// configuration binding — so any composition surface that implements this
/// interface can host a model.
/// </remarks>
public interface IDatabaseApplicationBuilder
{
    /// <summary>
    /// Gets the engines registered so far, in registration order.
    /// </summary>
    IReadOnlyList<IDatabaseEngine> Engines { get; }

    /// <summary>
    /// Registers a database engine the built application will serve and drive
    /// through the engine lifecycle contract.
    /// </summary>
    /// <param name="engine">The engine to register.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> is <see langword="null"/>.</exception>
    IDatabaseApplicationBuilder AddEngine(IDatabaseEngine engine);

    /// <summary>
    /// Registers a pre-built wire-protocol server as the application's endpoint.
    /// </summary>
    /// <param name="server">The server to run as the endpoint.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A server was already registered.</exception>
    IDatabaseApplicationBuilder AddServer(IDatabaseServer server);

    /// <summary>
    /// Registers a deferred wire-protocol server factory as the application's
    /// endpoint. The factory runs at <see cref="Build"/> time and receives the
    /// final registered engine list, so the server can be composed over engines
    /// that model verbs register after this call.
    /// </summary>
    /// <param name="configure">The factory that creates the server from the registered engines.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A server was already registered.</exception>
    IDatabaseApplicationBuilder AddServer(Func<IReadOnlyList<IDatabaseEngine>, IDatabaseServer> configure);

    /// <summary>
    /// Builds the database application from the registered engines and endpoint.
    /// </summary>
    /// <returns>The composed application, ready to start.</returns>
    /// <exception cref="InvalidOperationException">The application has already been built.</exception>
    IDatabaseApplication Build();
}

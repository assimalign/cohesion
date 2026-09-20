using System;

namespace Assimalign.Cohesion.Database;

/// <summary>Collects dependency-free engine intent for one database application.</summary>
/// <remarks>Model verbs defer construction until Build. Servers and workers belong to engine builders.</remarks>
public interface IDatabaseApplicationBuilder
{
    /// <summary>Registers a borrowed engine; its caller remains the disposal owner.</summary>
    /// <param name="engine">The existing engine.</param>
    /// <returns>This builder.</returns>
    IDatabaseApplicationBuilder AddEngine(IDatabaseEngine engine);

    /// <summary>Defers owned engine construction until the application's first Build attempt.</summary>
    /// <param name="configure">A factory receiving the engines already constructed in registration order.</param>
    /// <returns>This builder.</returns>
    /// <remarks>The factory transfers ownership of a fresh engine. It cleans up allocations if it throws before returning.</remarks>
    IDatabaseApplicationBuilder AddEngine(Func<IDatabaseApplicationContext, IDatabaseEngine> configure);

    /// <summary>Consumes this builder and publishes a complete, disposable application.</summary>
    /// <returns>The application with live engines and listeners awaiting Start.</returns>
    /// <exception cref="InvalidOperationException">Build was already attempted or composition is invalid.</exception>
    IDatabaseApplication Build();
}

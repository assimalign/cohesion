using System;

namespace Assimalign.Cohesion.Database;

/// <summary>Captures dependency-free engine composition for one build attempt.</summary>
/// <remarks>Worker and server factories run after the engine exists. Their products belong to the engine.</remarks>
public interface IDatabaseEngineBuilder
{
    /// <summary>Registers a factory for an engine-owned background worker.</summary>
    /// <param name="configure">The factory, invoked once against the constructed engine.</param>
    /// <returns>This builder.</returns>
    IDatabaseEngineBuilder AddWorker(Func<IDatabaseEngine, IDatabaseEngineWorker> configure);

    /// <summary>Registers a factory for an engine-owned server.</summary>
    /// <param name="configure">The factory, invoked once against the engine the server must front.</param>
    /// <returns>This builder.</returns>
    IDatabaseEngineBuilder AddServer(Func<IDatabaseEngine, IDatabaseServer> configure);

    /// <summary>Freezes composition and constructs the engine, workers, and servers.</summary>
    /// <returns>The operational engine, whose servers remain stopped until application startup.</returns>
    /// <exception cref="InvalidOperationException">A build was already attempted.</exception>
    IDatabaseEngine Build();
}

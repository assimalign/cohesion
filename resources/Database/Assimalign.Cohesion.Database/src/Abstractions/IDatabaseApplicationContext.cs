using System.Collections.Generic;

namespace Assimalign.Cohesion.Database;

/// <summary>Observes database composition without exposing registration machinery.</summary>
public interface IDatabaseApplicationContext
{
    /// <summary>Gets all distinct engines in composition order.</summary>
    IReadOnlyList<IDatabaseEngine> Engines { get; }

    /// <summary>Gets servers flattened from engines, followed by legacy borrowed server inputs.</summary>
    IReadOnlyList<IDatabaseServer> Servers { get; }

    /// <summary>Retrieves a borrowed engine by its ordinal name.</summary>
    /// <param name="name">The engine name.</param>
    /// <returns>The registered engine, without transferring ownership.</returns>
    /// <exception cref="KeyNotFoundException">No engine has that name.</exception>
    IDatabaseEngine GetEngine(string name);
}

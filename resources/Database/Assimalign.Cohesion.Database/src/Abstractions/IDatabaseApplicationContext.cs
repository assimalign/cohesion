using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database;

/// <summary>Observes database composition without exposing registration machinery.</summary>
/// <remarks>
/// One of the five interfaces the Database area keeps (the O34 composition seam,
/// <c>database-area.md</c>). Its engines and servers are the root bases, <see cref="DatabaseEngine"/>
/// and <see cref="DatabaseServer"/> (concrete-types plan, phase 6, #1262); a model's typed engine is
/// one call away through the generic
/// <see cref="DatabaseApplicationContextExtensions.GetEngine{TEngine}(IDatabaseApplicationContext, string)"/>.
/// </remarks>
public interface IDatabaseApplicationContext
{
    /// <summary>Gets all distinct engines in composition order.</summary>
    IReadOnlyList<DatabaseEngine> Engines { get; }

    /// <summary>Gets servers flattened from engines, followed by legacy borrowed server inputs.</summary>
    IReadOnlyList<DatabaseServer> Servers { get; }

    /// <summary>Retrieves a borrowed engine by its ordinal name.</summary>
    /// <param name="name">The engine name.</param>
    /// <returns>The registered engine, without transferring ownership.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <exception cref="KeyNotFoundException">No engine has that name.</exception>
    DatabaseEngine GetEngine(string name);
}

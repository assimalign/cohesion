using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Embedded;

/// <summary>
/// Options for composing an <see cref="EmbeddedDatabase"/>.
/// </summary>
public sealed class EmbeddedDatabaseOptions
{
    /// <summary>
    /// Gets the engines to embed, in registration order. Add engines created from
    /// their model factories, for example
    /// <c>KeyValueDatabaseEngine.Create(new() { RootPath = dataPath })</c>.
    /// </summary>
    public IList<IDatabaseEngine> Engines { get; } = new List<IDatabaseEngine>();
}

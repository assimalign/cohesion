using System.Collections.Generic;

namespace Assimalign.Cohesion.Database;

/// <summary>Describes one permission grant.</summary>
public interface IDatabaseSchemaGrant
{
    /// <summary>Gets the granted permission.</summary>
    Permission Permission { get; }

    /// <summary>Gets the schema object names covered by the grant.</summary>
    IReadOnlyList<string> Objects { get; }
}

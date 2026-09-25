using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Describes one permission grant.</summary>
public interface ISqlSchemaGrant
{
    /// <summary>Gets the granted permission.</summary>
    SqlPermission Permission { get; }

    /// <summary>Gets the schema object names covered by the grant.</summary>
    IReadOnlyList<string> Objects { get; }
}

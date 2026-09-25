using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Describes a database principal declaration.</summary>
public interface ISqlSchemaPrincipal
{
    /// <summary>Gets the principal name.</summary>
    string Name { get; }

    /// <summary>Gets the principal's grants.</summary>
    IReadOnlyList<ISqlSchemaGrant> Grants { get; }
}

using System.Collections.Generic;

namespace Assimalign.Cohesion.Database;

/// <summary>Describes a database principal declaration.</summary>
public interface IDatabaseSchemaPrincipal
{
    /// <summary>Gets the principal name.</summary>
    string Name { get; }

    /// <summary>Gets the principal's grants.</summary>
    IReadOnlyList<IDatabaseSchemaGrant> Grants { get; }
}

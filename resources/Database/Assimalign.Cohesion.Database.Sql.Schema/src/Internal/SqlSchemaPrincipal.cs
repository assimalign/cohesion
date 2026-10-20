using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>A database principal declaration.</summary>
/// <param name="Name">The principal name.</param>
/// <param name="Grants">The principal's grants.</param>
internal sealed record SqlSchemaPrincipal(
    string Name,
    IReadOnlyList<SqlSchemaGrant> Grants);

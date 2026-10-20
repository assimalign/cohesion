using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>One permission grant.</summary>
/// <param name="Permission">The granted permission.</param>
/// <param name="Objects">The schema object names covered by the grant.</param>
internal sealed record SqlSchemaGrant(SqlPermission Permission, IReadOnlyList<string> Objects);

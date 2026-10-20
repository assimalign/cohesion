using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>
/// The completed C# declaration of one logical database's schema: what
/// <see cref="SqlSchemaBuilder"/> recorded, as immutable snapshots the compiler reads. Internal
/// behind the opaque <see cref="SqlSchema"/> (concrete-types plan, §6.7): a public positional
/// record would expose a public constructor and a <c>with</c> clone around the builders'
/// validation.
/// </summary>
/// <param name="AllowsDestructiveChanges">Whether migration planning may include destructive operations.</param>
/// <param name="Types">The declared custom types.</param>
/// <param name="Tables">The declared tables.</param>
/// <param name="Principals">The declared database principals.</param>
/// <param name="Name">The logical database name.</param>
internal sealed record SqlSchemaDeclaration(
    bool AllowsDestructiveChanges,
    IReadOnlyList<SqlSchemaType> Types,
    IReadOnlyList<SqlSchemaTable> Tables,
    IReadOnlyList<SqlSchemaPrincipal> Principals,
    string Name);

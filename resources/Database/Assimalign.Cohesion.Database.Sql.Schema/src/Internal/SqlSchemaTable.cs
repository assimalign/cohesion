using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

/// <summary>A table declaration.</summary>
/// <param name="Name">The stable table name.</param>
/// <param name="RowType">The table row type whose complete shape the compiler inspects.</param>
/// <param name="Columns">The row members explicitly mentioned by column, key, index, or reference declarations.</param>
/// <param name="ColumnDefinitions">The typed column declarations.</param>
/// <param name="PrimaryKey">The primary-key member name.</param>
/// <param name="Indexes">The indexed member names.</param>
/// <param name="References">The declared references.</param>
/// <param name="Checks">The declared CHECK constraints, in declaration order.</param>
internal sealed record SqlSchemaTable(
    string Name,
    Type RowType,
    IReadOnlyList<string> Columns,
    IReadOnlyList<SqlSchemaColumn> ColumnDefinitions,
    string? PrimaryKey,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<SqlSchemaReference> References,
    IReadOnlyList<SqlSchemaCheck> Checks);

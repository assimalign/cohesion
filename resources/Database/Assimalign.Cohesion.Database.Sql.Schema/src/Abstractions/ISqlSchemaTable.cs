using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Describes a table declaration.</summary>
public interface ISqlSchemaTable
{
    /// <summary>Gets the stable table name.</summary>
    string Name { get; }

    /// <summary>Gets the table row type whose complete shape a schema compiler inspects.</summary>
    Type RowType { get; }

    /// <summary>Gets row members explicitly mentioned by column, key, index, or reference declarations.</summary>
    IReadOnlyList<string> Columns { get; }

    /// <summary>Gets the typed column declarations.</summary>
    IReadOnlyList<ISqlSchemaColumn> ColumnDefinitions { get; }

    /// <summary>Gets the primary-key member name.</summary>
    string? PrimaryKey { get; }

    /// <summary>Gets the indexed member names.</summary>
    IReadOnlyList<string> Indexes { get; }

    /// <summary>Gets the declared references.</summary>
    IReadOnlyList<ISqlSchemaReference> References { get; }
}

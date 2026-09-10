using System;

namespace Assimalign.Cohesion.Database;

/// <summary>Describes a typed column selected from a schema row type.</summary>
public interface IDatabaseSchemaColumn
{
    /// <summary>Gets the column name.</summary>
    string Name { get; }

    /// <summary>Gets the declared CLR value type.</summary>
    Type ClrType { get; }

    /// <summary>Gets a value indicating whether null values are permitted.</summary>
    bool IsNullable { get; }
}

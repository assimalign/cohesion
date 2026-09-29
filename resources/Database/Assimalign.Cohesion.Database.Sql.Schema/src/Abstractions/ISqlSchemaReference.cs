using System;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Describes a table reference.</summary>
public interface ISqlSchemaReference
{
    /// <summary>Gets the foreign-key member name.</summary>
    string Member { get; }

    /// <summary>Gets the referenced table row type.</summary>
    Type TargetType { get; }
}

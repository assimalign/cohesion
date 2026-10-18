namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>Represents CREATE SCHEMA for a SQL namespace, distinct from a compiled schema.</summary>
public sealed class SqlCreateSchemaExpression : SqlQueryExpression
{
    internal SqlCreateSchemaExpression(string name, bool ifNotExists, Location location)
        : base(SqlQueryCommandType.Create, null, location)
    {
        Name = name;
        IfNotExists = ifNotExists;
    }

    /// <summary>Gets the unqualified namespace name.</summary>
    public string Name { get; }

    /// <summary>Gets whether an existing namespace is accepted.</summary>
    public bool IfNotExists { get; }
}

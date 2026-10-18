namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>Represents DROP SCHEMA for a SQL namespace.</summary>
public sealed class SqlDropSchemaExpression : SqlQueryExpression
{
    internal SqlDropSchemaExpression(string name, bool ifExists, bool cascade, Location location)
        : base(SqlQueryCommandType.Drop, null, location)
    {
        Name = name;
        IfExists = ifExists;
        Cascade = cascade;
    }

    /// <summary>Gets the unqualified namespace name.</summary>
    public string Name { get; }

    /// <summary>Gets whether an absent namespace is accepted.</summary>
    public bool IfExists { get; }

    /// <summary>Gets whether dependent objects are dropped; false means RESTRICT.</summary>
    public bool Cascade { get; }
}

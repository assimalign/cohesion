using System.Diagnostics;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents a relation's qualified wildcard in a SELECT list.</summary>
[DebuggerDisplay("{TableName,nq}.*")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class SqlQualifiedStarExpression : SqlExpression
{
    internal SqlQualifiedStarExpression(string tableName, string? schemaName, Location? location)
        : base(location)
    {
        TableName = tableName;
        SchemaName = schemaName;
    }

    /// <summary>Gets the relation name or alias preceding the wildcard.</summary>
    public string TableName { get; }

    /// <summary>Gets the optional schema qualifier.</summary>
    public string? SchemaName { get; }

    private sealed class DebugView
    {
        private readonly SqlQualifiedStarExpression _expression;

        public DebugView(SqlQualifiedStarExpression expression)
        {
            _expression = expression;
        }

        public string TableName => _expression.TableName;

        public string? SchemaName => _expression.SchemaName;

        public Location? Location => _expression.Location;
    }
}

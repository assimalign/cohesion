using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents the standard NEXT VALUE FOR sequence value expression.</summary>
public sealed class SqlNextValueExpression : SqlExpression
{
    internal SqlNextValueExpression(SqlTableReference sequence, Location location) : base(location) { Sequence = sequence; }

    /// <summary>Gets the qualified sequence name.</summary>
    public SqlTableReference Sequence { get; }
}

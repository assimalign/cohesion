using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents CREATE SEQUENCE and its explicitly written options.</summary>
public sealed class SqlCreateSequenceExpression : SqlQueryExpression
{
    internal SqlCreateSequenceExpression(SqlTableReference sequence, bool guard, IReadOnlyList<SqlSequenceOption> options, Location location)
        : base(SqlQueryCommandType.Create, null, location)
    { Sequence = sequence; IfNotExists = guard; Options = options; }

    /// <summary>Gets the qualified sequence name.</summary>
    public SqlTableReference Sequence { get; }

    /// <summary>Gets whether the existence guard is specified.</summary>
    public bool IfNotExists { get; }

    /// <summary>Gets options in written order.</summary>
    public IReadOnlyList<SqlSequenceOption> Options { get; }
}

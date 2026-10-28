using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents ALTER SEQUENCE and its explicitly written options.</summary>
public sealed class SqlAlterSequenceExpression : SqlQueryExpression
{
    internal SqlAlterSequenceExpression(SqlTableReference sequence, bool guard, IReadOnlyList<SqlSequenceOption> options, Location location)
        : base(SqlQueryCommandType.Alter, null, location)
    { Sequence = sequence; IfExists = guard; Options = options; }

    /// <summary>Gets the qualified sequence name.</summary>
    public SqlTableReference Sequence { get; }

    /// <summary>Gets whether the existence guard is specified.</summary>
    public bool IfExists { get; }

    /// <summary>Gets options in written order.</summary>
    public IReadOnlyList<SqlSequenceOption> Options { get; }
}

using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents DROP SEQUENCE with a qualified name list and dependency policy.</summary>
public sealed class SqlDropSequenceExpression : SqlQueryExpression
{
    internal SqlDropSequenceExpression(IReadOnlyList<SqlTableReference> sequences, bool ifExists, bool cascade, Location location)
        : base(SqlQueryCommandType.Drop, null, location)
    { Sequences = sequences; IfExists = ifExists; Cascade = cascade; }

    /// <summary>Gets the sequence names in written order.</summary>
    public IReadOnlyList<SqlTableReference> Sequences { get; }

    /// <summary>Gets whether absent sequences are accepted.</summary>
    public bool IfExists { get; }

    /// <summary>Gets whether dependent objects may be removed.</summary>
    public bool Cascade { get; }
}

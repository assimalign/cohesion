using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>A table export to the caller's standard output stream.</summary>
public sealed class SqlCopyToExpression : SqlQueryExpression
{
    internal SqlCopyToExpression(SqlTableReference table, IReadOnlyList<string> columns, SqlCopyOptions options, Location? location)
        : base(SqlQueryCommandType.CopyTo, null, location)
    {
        Table = table;
        Columns = Array.AsReadOnly(columns.ToArray());
        Options = options;
    }

    /// <summary>Gets the source table.</summary>
    public SqlTableReference Table { get; }

    /// <summary>Gets the ordered column list; empty selects every live column.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets the validated export options, including defaults.</summary>
    public SqlCopyOptions Options { get; }
}

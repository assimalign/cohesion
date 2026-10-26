using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents TRUNCATE's table list, identity policy and foreign-key policy.</summary>
public sealed class SqlTruncateExpression : SqlQueryExpression
{
    internal SqlTruncateExpression(IReadOnlyList<SqlTruncateTable> tables, bool restartIdentity,
        bool cascade, Location? location) : base(SqlQueryCommandType.Truncate, null, location)
    {
        Tables = Array.AsReadOnly(tables.ToArray());
        RestartIdentity = restartIdentity;
        Cascade = cascade;
    }

    /// <summary>Gets the ordered targets, including each written ONLY modifier.</summary>
    public IReadOnlyList<SqlTruncateTable> Tables { get; }

    /// <summary>Gets whether identity allocation restarts at its declared start; false means CONTINUE IDENTITY.</summary>
    public bool RestartIdentity { get; }

    /// <summary>Gets whether referencing tables join the truncation recursively; false means RESTRICT.</summary>
    public bool Cascade { get; }
}

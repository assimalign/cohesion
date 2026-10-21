using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Shared by copies of the statement context. Definitions are read under object intent locks;
// this state never survives into the next statement, including in an explicit transaction.
internal sealed class SqlStatementReferences
{
    internal Dictionary<ulong, IReadOnlyList<(SqlCatalogTable Child, SqlCatalogConstraint Constraint)>> Incoming { get; } = [];
    internal Dictionary<SqlCatalogConstraint, SqlCatalogTable> Parents { get; } = [];
    internal HashSet<ulong> IncomingLocks { get; } = [];
    internal HashSet<ulong> OutgoingLocks { get; } = [];
}

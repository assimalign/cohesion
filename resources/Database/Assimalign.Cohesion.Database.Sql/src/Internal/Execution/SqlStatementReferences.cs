using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Shared by copies of the statement context. Definitions are read under object intent locks;
// this state never survives into the next statement, including in an explicit transaction.
internal sealed class SqlStatementReferences
{
    internal Dictionary<ulong, IReadOnlyList<SqlBoundReference>> Incoming { get; } = [];
    internal Dictionary<SqlCatalogConstraint, SqlCatalogTable> Parents { get; } = [];
    internal Dictionary<SqlCatalogConstraint, SqlBoundReference> Bindings { get; } = [];
    internal Dictionary<SqlBoundConstraintKey, SqlConstraintLookup> Lookups { get; } = [];
    internal Dictionary<SqlCatalogTable, List<SqlLiveIndex>> LiveIndexes { get; } = [];
    internal HashSet<ulong> IncomingLocks { get; } = [];
    internal HashSet<ulong> OutgoingLocks { get; } = [];
}

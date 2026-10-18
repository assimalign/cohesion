using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Optional statement binding state; absent for ordinary stored-only queries.</summary>
internal sealed class SqlQueryScopeState
{
    internal Stack<(SqlSelectExpression? Select, SqlTableReference? Dml, SqlDerivedTableReference? Derived)> Scopes { get; } = new();
    internal SqlCorrelationScope? OuterScope { get; set; }
    internal HashSet<int>? OuterDependencies { get; set; }
    internal int NextScopeId { get; set; }
    internal Dictionary<(SqlSelectExpression? Owner, SqlRelationReference Reference), SqlRelationSource> Relations { get; } = new();
}

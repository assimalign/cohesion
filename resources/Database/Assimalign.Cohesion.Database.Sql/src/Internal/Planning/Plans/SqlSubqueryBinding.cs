using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A child query, the slot its values fill, and the result type and collation its values carry.
/// </summary>
internal sealed record SqlSubqueryBinding(
    SqlPlan Query,
    int Slot,
    DatabaseType Type,
    Collation Collation,
    SqlSubqueryKind Kind,
    bool IsNegated,
    int ScopeId,
    bool IsCorrelated);

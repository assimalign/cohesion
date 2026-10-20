using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A subquery use site as the expression binder sees it: the slot its values fill, how its use site
/// reduces them, and the collation they carry, which a comparison against them uses.
/// </summary>
/// <param name="Id">The slot, numbered from zero across every nesting level of one statement.</param>
/// <param name="Kind">How the use site reduces the subquery's rows.</param>
/// <param name="Collation">The collation of the subquery's output column.</param>
/// <param name="Type">
/// The type of the subquery's values: its output column's, or BOOLEAN for <c>EXISTS</c>; what a
/// function call over a scalar subquery resolves its overload by.
/// </param>
internal sealed record SqlSubquerySlot(int Id, SqlSubqueryKind Kind, Collation Collation, DatabaseType Type, DatabaseTypeInfo? TypeInfo = null, string? DomainName = null);

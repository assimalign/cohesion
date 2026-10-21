using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>One bound <c>WHEN</c> clause of a <see cref="SqlBoundCase"/>.</summary>
/// <param name="Condition">The bound condition, or the bound value a simple <c>CASE</c> compares with its input.</param>
/// <param name="Result">The bound result.</param>
/// <param name="Collation">The collation of the input and the value, for a simple <c>CASE</c>; unused for a searched one.</param>
/// <param name="Location">The source condition's span, when available.</param>
internal readonly record struct SqlBoundWhen(SqlBoundExpression Condition, SqlBoundExpression Result, SqlBoundCollation Collation, Location? Location = null);

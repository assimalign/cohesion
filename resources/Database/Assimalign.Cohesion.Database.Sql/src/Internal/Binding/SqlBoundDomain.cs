namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A stored column's domain bound against one table version (phase E3): the domain's check as a
/// call over the column's value, which every write that assigns the column evaluates, or the reason
/// the domain does not resolve, which fails such a write with <c>COHSQLE010</c>.
/// </summary>
/// <param name="Ordinal">The column's ordinal in the table version.</param>
/// <param name="Name">The domain's name.</param>
/// <param name="Check">
/// The domain's check called on the column's value, bound once; null for a domain without a check
/// and for an unresolved one. Only FALSE refuses the value.
/// </param>
/// <param name="Unresolved">Why the domain does not resolve; null when it does.</param>
internal sealed record SqlBoundDomain(int Ordinal, string Name, SqlBoundExpression? Check, SqlUnresolvedDomain? Unresolved);

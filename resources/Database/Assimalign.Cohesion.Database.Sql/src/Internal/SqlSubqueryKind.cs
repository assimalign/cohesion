namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The cardinality and reduction required by a subquery's use site.</summary>
internal enum SqlSubqueryKind { Scalar, Exists, Set }

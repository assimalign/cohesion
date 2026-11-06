// Deviates from the repo RootNamespace rule per shared-source design: the stateless SQL target
// limit is compiled into the parser and binder without exposing an implementation-only API.
namespace Assimalign.Cohesion.Database.Sql.Language.Internal;

internal static class SqlTargetListLimits
{
    // PostgreSQL src/backend/parser/parse_node.c:79-83 uses MaxTupleAttributeNumber.
    internal const int MaximumTargets = 1664;
    internal const string Code = "CS05097";
}

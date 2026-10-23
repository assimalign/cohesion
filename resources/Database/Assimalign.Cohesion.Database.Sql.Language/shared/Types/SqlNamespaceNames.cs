// Deviates from the repo RootNamespace rule per shared-source design: the stateless SQL default
// namespace spelling is shared by the language, runtime, compiled schema and SDK renderer.
namespace Assimalign.Cohesion.Database.Sql.Language.Internal;

internal static class SqlNamespaceNames
{
    internal const string Default = "dbo";
}

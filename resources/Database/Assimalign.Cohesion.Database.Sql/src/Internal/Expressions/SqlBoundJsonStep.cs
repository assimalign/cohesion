using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>One selector and its statically chosen overload in a bound arrow chain.</summary>
internal readonly record struct SqlBoundJsonStep(SqlBoundExpression Selector, DatabaseType SelectorType, bool IsIndex, Location? Location);

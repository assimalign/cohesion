using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Tests;

internal static class SqlDefinitionSyntax
{
    // Malformed typed declarations cannot be obtained from a successful text parse.
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    internal static extern SqlCreateFunctionExpression Function(string name, IReadOnlyList<SqlFunctionParameter> parameters,
        string returnType, string volatility, bool isStrict, SqlExpression body, Location location);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    internal static extern SqlCreateDomainExpression Domain(string name, string baseType, SqlExpression? defaultValue,
        bool isNotNull, string? constraintName, SqlExpression? check, Location location);
}

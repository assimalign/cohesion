using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

internal static class SqlPartialIndexRegistrations
{
    // Public CreateDomain refuses volatile checks. Exercise the defensive rebind boundary
    // with the changed registration descriptor directly, without weakening that API guard.
    internal static SqlType ChangedDomain(SqlScalarFunction check)
        => Domain("native_int", SqlType.Integer.Storage, false, SqlType.Integer, check);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern SqlType Domain(string name, DatabaseTypeInfo storage, bool isPseudo, SqlType baseType, SqlScalarFunction check);
}

using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

// Read existing storage accounting without adding public API or a foreign friend grant.
// UnsafeAccessor binds statically and is NativeAOT-compatible.
internal static class SqlStorageMemoryProbe
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PreImageBytes")]
    internal static extern long PreImageBytes(Assimalign.Cohesion.Database.Storage.Storage storage);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PeakPreImageBytes")]
    internal static extern long PeakPreImageBytes(Assimalign.Cohesion.Database.Storage.Storage storage);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SpilledPreImages")]
    internal static extern long SpilledPreImages(Assimalign.Cohesion.Database.Storage.Storage storage);
}

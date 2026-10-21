namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Delivers a value whose producer can retain or reuse its binary array.</summary>
internal static class SqlBinaryValue
{
    // Clone also gives an empty binary value its own array.
    internal static object? CopyShared(object? value) => value is byte[] bytes ? bytes.Clone() : value;
}

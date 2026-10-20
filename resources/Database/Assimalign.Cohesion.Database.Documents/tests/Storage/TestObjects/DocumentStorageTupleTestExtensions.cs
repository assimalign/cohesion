using System;

namespace Assimalign.Cohesion.Database.Documents.Storage.Tests;


internal static class DocumentStorageTupleTestExtensions
{
    internal static ReadOnlyMemory<byte> ToUtf8(this string value)
    {
        return System.Text.Encoding.UTF8.GetBytes(value);
    }

    internal static string ToStringValue(this ReadOnlyMemory<byte> value)
    {
        return System.Text.Encoding.UTF8.GetString(value.Span);
    }
}

using System;

namespace Assimalign.Cohesion.Database.Documents.Storage.Tests;


internal static partial class DocumentStorageTupleTestExtensions
{
    extension(string value)
    {
        internal ReadOnlyMemory<byte> ToUtf8()
        {
            return System.Text.Encoding.UTF8.GetBytes(value);
        }
    }

    extension(ReadOnlyMemory<byte> value)
    {
        internal string ToStringValue()
        {
            return System.Text.Encoding.UTF8.GetString(value.Span);
        }
    }
}

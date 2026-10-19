using System.IO;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

internal sealed class RecoveryTruncationStream : MemoryStream
{
    internal bool FailTruncation { get; set; }

    public override void SetLength(long value)
    {
        if (FailTruncation)
        {
            // A provider can change bytes before throwing, without changing the file length.
            GetBuffer()[37] ^= 0xFF;
            throw new IOException("Injected truncation failure after corrupting the prefix.");
        }
        base.SetLength(value);
    }
}

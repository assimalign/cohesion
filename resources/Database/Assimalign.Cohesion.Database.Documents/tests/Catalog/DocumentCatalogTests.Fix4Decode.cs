using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Escaped permitted keys admit all cold decoding buffers (#1808)")]
    public void Read_Fix4EscapedKey_ShouldCoverColdDecodeAllocations()
    {
        // Arrange: 510 U+0101 characters encode to exactly 1024 key bytes. The
        // unindexed tokens keep the DOM's 4096-byte buffer live during decoding.
        byte[] content = Encoding.UTF8.GetBytes("{\"v\":\"" + string.Concat(Enumerable.Repeat("\\u0101", 510)) +
            "\",\"pad\":[" + string.Join(',', Enumerable.Repeat("0", 256)) + "]}");
        var metadata = new DocumentIndexMetadata(Guid.NewGuid(), "ix", [DocumentIndexKey.Scalar("v")], false, false, 1000);
        for (int i = 0; i < 16; i++) { DocumentIndexKeys.Read(content, metadata, out _, reserve: _ => { }); }
        var pins = new List<byte[]>();
        try
        {
            // Empty every relevant bucket, proving the final rental was fresh.
            for (int size = 64; size <= 8192; size *= 2)
            {
                while (true)
                {
                    long beforeRent = GC.GetAllocatedBytesForCurrentThread();
                    byte[] pin = ArrayPool<byte>.Shared.Rent(size);
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeRent;
                    pins.Add(pin);
                    if (allocated >= size) { break; }
                }
            }
            long charge = 0;
            Action<long> reserve = bytes => charge += bytes;
            long before = GC.GetAllocatedBytesForCurrentThread();
            // Act.
            var keys = DocumentIndexKeys.Read(content, metadata, out _, reserve: reserve);
            long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            // Assert: the content array was allocated before this measurement.
            keys.ShouldHaveSingleItem().Length.ShouldBe(1024);
            _output.WriteLine($"ESCAPED_KEY|charge={charge}|allocated={allocatedBytes}");
            allocatedBytes.ShouldBeLessThanOrEqualTo(charge);
            GC.KeepAlive(keys);
        }
        finally { foreach (byte[] pin in pins) { ArrayPool<byte>.Shared.Return(pin); } }
    }
}

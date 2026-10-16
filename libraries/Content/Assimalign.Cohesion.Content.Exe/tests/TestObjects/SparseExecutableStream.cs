using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Exe.Tests;

// A header-only synthetic sparse source: no 40 GiB backing allocation is made.
internal sealed class SparseExecutableStream : MemoryStream
{
    internal SparseExecutableStream(byte[] header) : base(header) { }
    public override long Length => 40L * 1024 * 1024 * 1024;
    internal int BytesRead { get; private set; }
    public override int Read(Span<byte> buffer)
    {
        var count = base.Read(buffer);
        BytesRead += count;
        return count;
    }
}

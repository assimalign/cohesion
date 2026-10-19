using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

internal sealed class RecoveryRecordingStream : MemoryStream
{
    internal List<(long Offset, long Lsn, uint Checksum)> Writes { get; } = [];

    public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

    public override void Write(byte[] buffer, int offset, int count)
    {
        var bytes = buffer.AsSpan(offset, count);
        Writes.Add((Position, BinaryPrimitives.ReadInt64LittleEndian(bytes[Page.LsnFieldOffset..]), Crc32C.Compute(bytes)));
        base.Write(buffer, offset, count);
    }
}

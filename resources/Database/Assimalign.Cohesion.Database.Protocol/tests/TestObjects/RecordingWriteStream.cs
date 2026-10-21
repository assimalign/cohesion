using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

internal sealed class RecordingWriteStream : MemoryStream
{
    internal List<int> WriteLengths { get; } = new();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteLengths.Add(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }
}

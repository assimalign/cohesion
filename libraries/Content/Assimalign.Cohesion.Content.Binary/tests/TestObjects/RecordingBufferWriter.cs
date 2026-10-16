using System;
using System.Buffers;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Binary.Tests;

internal sealed class RecordingBufferWriter : IBufferWriter<byte>
{
    private readonly ArrayBufferWriter<byte> _output = new ArrayBufferWriter<byte>();
    internal List<int> Requests { get; } = [];
    internal List<int> Advances { get; } = [];
    internal ReadOnlySpan<byte> WrittenSpan => _output.WrittenSpan;
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Requests.Add(sizeHint);
        return _output.GetSpan(sizeHint);
    }
    public Memory<byte> GetMemory(int sizeHint = 0) => throw new InvalidOperationException("The writer must use GetSpan.");
    public void Advance(int count)
    {
        Advances.Add(count);
        _output.Advance(count);
    }
}

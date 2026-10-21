using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

internal sealed class InterruptedPayloadStream : MemoryStream
{
    private readonly bool _fail;
    internal TaskCompletionSource PayloadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal InterruptedPayloadStream(bool fail) : base(new byte[] { 0, 0, 0, 3, (byte)ProtocolMessageType.Error })
    {
        _fail = fail;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position < Length) { return await base.ReadAsync(buffer, cancellationToken); }
        PayloadEntered.TrySetResult();
        if (_fail) { throw new IOException("Injected payload read failure."); }
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}

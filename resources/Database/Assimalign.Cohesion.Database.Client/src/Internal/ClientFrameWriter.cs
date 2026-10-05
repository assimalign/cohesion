using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Internal;

internal sealed class ClientFrameWriter : ProtocolFrameWriter
{
    private readonly ProtocolFrameWriter _writer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientFrameWriter"/> class.
    /// </summary>
    /// <param name="writer">The underlying protocol frame writer whose completed-pipe failures are translated.</param>
    public ClientFrameWriter(ProtocolFrameWriter writer)
    {
        _writer = writer;
    }

    protected override async ValueTask WriteFrameCoreAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            await _writer.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new IOException("The connection closed while sending a request.", exception);
        }
    }

    protected override async ValueTask FlushCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new IOException("The connection closed while flushing a request.", exception);
        }
    }

    protected override ValueTask DisposeAsyncCore() => _writer.DisposeAsync();
}

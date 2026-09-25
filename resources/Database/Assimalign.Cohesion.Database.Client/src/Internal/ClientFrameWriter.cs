using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Internal;

internal sealed class ClientFrameWriter : IProtocolFrameWriter
{
    private readonly IProtocolFrameWriter _writer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientFrameWriter"/> class.
    /// </summary>
    /// <param name="writer">The underlying protocol frame writer whose completed-pipe failures are translated.</param>
    public ClientFrameWriter(IProtocolFrameWriter writer)
    {
        _writer = writer;
    }

    public async ValueTask WriteFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken = default)
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

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
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

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}

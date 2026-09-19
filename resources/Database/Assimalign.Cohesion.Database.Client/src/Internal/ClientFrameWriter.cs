using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

internal sealed class ClientFrameWriter(IProtocolFrameWriter writer) : IProtocolFrameWriter
{
    public async ValueTask WriteFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken = default)
    {
        try
        {
            await writer.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
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
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new IOException("The connection closed while flushing a request.", exception);
        }
    }

    public ValueTask DisposeAsync() => writer.DisposeAsync();
}

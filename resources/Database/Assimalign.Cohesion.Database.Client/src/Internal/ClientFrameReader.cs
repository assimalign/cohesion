using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

// Completed transport pipes can report InvalidOperationException instead of IOException.
// Translate only frame I/O, so exceptions from caller streams retain their original meaning.
internal sealed class ClientFrameReader(IProtocolFrameReader reader) : IProtocolFrameReader
{
    public async ValueTask<ProtocolFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new IOException("The connection closed while reading a response.", exception);
        }
    }

    public ValueTask DisposeAsync() => reader.DisposeAsync();
}

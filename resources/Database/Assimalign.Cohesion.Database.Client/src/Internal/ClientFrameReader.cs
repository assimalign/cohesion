using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Internal;

// Completed transport pipes can report InvalidOperationException instead of IOException.
// Translate only frame I/O, so exceptions from caller streams retain their original meaning.
internal sealed class ClientFrameReader : IProtocolFrameReader
{
    private readonly IProtocolFrameReader _reader;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientFrameReader"/> class.
    /// </summary>
    /// <param name="reader">The underlying protocol frame reader whose completed-pipe failures are translated.</param>
    public ClientFrameReader(IProtocolFrameReader reader)
    {
        _reader = reader;
    }

    public async ValueTask<ProtocolFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new IOException("The connection closed while reading a response.", exception);
        }
    }

    public ValueTask DisposeAsync() => _reader.DisposeAsync();
}

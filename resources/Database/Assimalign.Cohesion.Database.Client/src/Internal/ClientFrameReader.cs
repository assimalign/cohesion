using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Internal;

// Completed transport pipes can report InvalidOperationException instead of IOException.
// Translate only frame I/O, so exceptions from caller streams retain their original meaning.
[global::System.Diagnostics.DebuggerDisplay("ClientFrameReader")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Client.Internal.ClientFrameReader.DebugView))]
internal sealed class ClientFrameReader : ProtocolFrameReader
{
    private readonly ProtocolFrameReader _reader;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientFrameReader"/> class.
    /// </summary>
    /// <param name="reader">The underlying protocol frame reader whose completed-pipe failures are translated.</param>
    public ClientFrameReader(ProtocolFrameReader reader)
    {
        _reader = reader;
    }

    protected override async ValueTask<ProtocolFrame?> ReadFrameCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new IOException(ErrorMessages.ResponseReadClosed, exception);
        }
    }

    protected override ValueTask DisposeAsyncCore() => _reader.DisposeAsync();

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Client.Internal.ClientFrameReader _target;

        public DebugView(global::Assimalign.Cohesion.Database.Client.Internal.ClientFrameReader value)
        {
            _target = value;
        }

        public bool SupportsBorrowedPayloads => _target.SupportsBorrowedPayloads;
    }
}

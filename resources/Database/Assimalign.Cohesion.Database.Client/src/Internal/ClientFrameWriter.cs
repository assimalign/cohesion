using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Internal;

[global::System.Diagnostics.DebuggerDisplay("ClientFrameWriter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Client.Internal.ClientFrameWriter.DebugView))]
internal sealed class ClientFrameWriter : ProtocolFrameWriter
{
    private readonly ProtocolFrameWriter _writer;
    private readonly bool _documentCommands;

    internal bool RequiresTransactionStatus { get; private set; }

    internal void BeginExchange() => RequiresTransactionStatus = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientFrameWriter"/> class.
    /// </summary>
    /// <param name="writer">The underlying protocol frame writer whose completed-pipe failures are translated.</param>
    /// <param name="family">The immutable family whose request identifiers are observed.</param>
    public ClientFrameWriter(ProtocolFrameWriter writer, ProtocolMessageFamily family)
    {
        _writer = writer;
        _documentCommands = family.Capabilities.Contains(ProtocolCapability.TransactionStatus) &&
            !family.Supports((ProtocolMessageType)5) && family.Supports((ProtocolMessageType)69);
    }

    protected override async ValueTask WriteFrameCoreAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            await _writer.WriteFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            // Execute (5), Graph ExecutePaths / Documents Execute (64), and Documents Command (69).
            // Observe the request, including application exchanges using the default constructor.
            if ((byte)frame.Type is 5 or 64 || (_documentCommands && (byte)frame.Type == 69)) { RequiresTransactionStatus = true; }
        }
        catch (InvalidOperationException exception)
        {
            throw new IOException(ErrorMessages.FrameWriterWriteFrameCoreAsyncIO, exception);
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
            throw new IOException(ErrorMessages.ClientFrameWriterFlushCoreAsyncIO, exception);
        }
    }

    protected override ValueTask DisposeAsyncCore() => _writer.DisposeAsync();

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Client.Internal.ClientFrameWriter value)
        {
            _ = value;
        }
    }
}

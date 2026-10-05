using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>A framed stream permanently bound to one endpoint's message family.</summary>
/// <remarks>The channel validates identifiers in both directions. The endpoint's session
/// owns message ordering and payload codecs. A connection cannot switch families.</remarks>
public sealed class ProtocolChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    /// <summary>Binds the stream to its endpoint's family.</summary>
    /// <param name="stream">The connected duplex stream.</param>
    /// <param name="family">The immutable family selected by the endpoint.</param>
    /// <param name="leaveOpen">Whether disposal leaves the transport stream open.</param>
    /// <exception cref="ArgumentNullException">The stream or family is null.</exception>
    public ProtocolChannel(Stream stream, ProtocolMessageFamily family, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(family);
        _stream = stream;
        _leaveOpen = leaveOpen;
        Family = family;
        Reader = new FamilyReader(ProtocolFrameReader.Create(stream, leaveOpen: true), family);
        Writer = new FamilyWriter(ProtocolFrameWriter.Create(stream, leaveOpen: true), family);
    }

    /// <summary>Gets the family fixed when the channel was created.</summary>
    public ProtocolMessageFamily Family { get; }

    /// <summary>Gets the reader that rejects identifiers outside the bound family.</summary>
    public ProtocolFrameReader Reader { get; }

    /// <summary>Gets the writer that rejects identifiers outside the bound family.</summary>
    public ProtocolFrameWriter Writer { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Reader.DisposeAsync().ConfigureAwait(false);
        await Writer.DisposeAsync().ConfigureAwait(false);
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void Validate(ProtocolMessageFamily family, ProtocolMessageType type)
    {
        if (!family.Supports(type))
        {
            throw new ProtocolException($"Message identifier {(byte)type} is not defined by endpoint family '{family.Name}'.");
        }
    }

    private sealed class FamilyReader : ProtocolFrameReader
    {
        private readonly ProtocolFrameReader _reader;
        private readonly ProtocolMessageFamily _family;

        /// <summary>
        /// Initializes a new instance of the <see cref="FamilyReader"/> class.
        /// </summary>
        /// <param name="reader">The framed reader whose frames are validated.</param>
        /// <param name="family">The family every read identifier must belong to.</param>
        public FamilyReader(ProtocolFrameReader reader, ProtocolMessageFamily family)
        {
            _reader = reader;
            _family = family;
        }

        protected override async ValueTask<ProtocolFrame?> ReadFrameCoreAsync(CancellationToken cancellationToken)
        {
            ProtocolFrame? frame = await _reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is { } value)
            {
                Validate(_family, value.Type);
            }
            return frame;
        }

        protected override ValueTask DisposeAsyncCore() => _reader.DisposeAsync();
    }

    private sealed class FamilyWriter : ProtocolFrameWriter
    {
        private readonly ProtocolFrameWriter _writer;
        private readonly ProtocolMessageFamily _family;

        /// <summary>
        /// Initializes a new instance of the <see cref="FamilyWriter"/> class.
        /// </summary>
        /// <param name="writer">The framed writer that receives validated frames.</param>
        /// <param name="family">The family every written identifier must belong to.</param>
        public FamilyWriter(ProtocolFrameWriter writer, ProtocolMessageFamily family)
        {
            _writer = writer;
            _family = family;
        }

        protected override ValueTask WriteFrameCoreAsync(ProtocolFrame frame, CancellationToken cancellationToken)
        {
            Validate(_family, frame.Type);
            return _writer.WriteFrameAsync(frame, cancellationToken);
        }

        protected override ValueTask FlushCoreAsync(CancellationToken cancellationToken) => _writer.FlushAsync(cancellationToken);
        protected override ValueTask DisposeAsyncCore() => _writer.DisposeAsync();
    }
}

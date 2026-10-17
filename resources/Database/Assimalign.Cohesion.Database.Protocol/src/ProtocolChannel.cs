using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private bool _agreementSet;
    private readonly string?[] _requiredCapabilities = new string?[256];
    private readonly HashSet<string> _capabilities = new(StringComparer.Ordinal);

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
        _requiredCapabilities[(byte)ProtocolMessageType.Diagnostics] = ProtocolCapability.Diagnostics.Name;
        foreach (var capability in family.Capabilities)
        {
            foreach (byte type in capability.MessageTypes) { _requiredCapabilities[type] = capability.Name; }
        }
        Reader = new FamilyReader(ProtocolFrameReader.Create(stream, leaveOpen: true), this);
        Writer = new FamilyWriter(ProtocolFrameWriter.Create(stream, leaveOpen: true), this);
    }

    /// <summary>Gets the family fixed when the channel was created.</summary>
    public ProtocolMessageFamily Family { get; }

    /// <summary>Gets the reader that rejects identifiers outside the bound family.</summary>
    public ProtocolFrameReader Reader { get; }

    /// <summary>Gets the writer that rejects identifiers outside the bound family.</summary>
    public ProtocolFrameWriter Writer { get; }

    /// <summary>Gets the immutable negotiated capability names.</summary>
    public IReadOnlyList<string> Capabilities { get; private set; } = Array.Empty<string>();

    /// <summary>Selects the server's intersection with the peer's advertised capabilities.</summary>
    /// <param name="version">The agreed version.</param>
    /// <param name="peerCapabilities">The peer's advertised names.</param>
    /// <returns>The Ready agreement.</returns>
    /// <exception cref="ProtocolException">The agreement is invalid or was already set.</exception>
    public ProtocolReadyMessage Negotiate(ProtocolVersion version, IReadOnlyList<string> peerCapabilities)
    {
        var agreement = new ProtocolReadyMessage(version, version.Minor >= 1
            ? Array.AsReadOnly(Family.Capabilities.Where(capability => peerCapabilities.Contains(capability.Name, StringComparer.Ordinal)).Select(capability => capability.Name).ToArray())
            : Array.Empty<string>());
        SetAgreement(agreement);
        return agreement;
    }

    /// <summary>Applies a validated Ready agreement once, enabling gated frames in both directions.</summary>
    /// <param name="agreement">The server's agreement.</param>
    /// <exception cref="ProtocolException">The agreement is invalid, unsupported, or was already set.</exception>
    public void SetAgreement(ProtocolReadyMessage agreement)
    {
        if (_agreementSet) { throw new ProtocolException(ErrorMessages.AgreementAlreadySet); }
        _ = agreement.Encode();
        if (!ProtocolVersion.TryNegotiate(agreement.Version, out var supported) || supported != agreement.Version ||
            agreement.Capabilities.Any(name => !Family.Capabilities.Any(capability => capability.Name == name)))
        {
            throw new ProtocolException(ErrorMessages.ReadyInvalid);
        }
        Capabilities = Array.AsReadOnly(agreement.Capabilities.ToArray());
        _capabilities.UnionWith(Capabilities);
        _agreementSet = true;
    }

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

    private void Validate(ProtocolMessageType type)
    {
        if (!Family.Supports(type))
        {
            throw new ProtocolException($"Message identifier {(byte)type} is not defined by endpoint family '{Family.Name}'.");
        }
    }

    private void ValidateCapability(ProtocolMessageType type)
    {
        string? required = _requiredCapabilities[(byte)type];
        if (required is not null && !_capabilities.Contains(required))
        {
            throw new ProtocolException(ErrorMessages.CapabilityNotNegotiated);
        }
    }

    private sealed class FamilyReader : ProtocolFrameReader
    {
        private readonly ProtocolFrameReader _reader;
        private readonly ProtocolChannel _channel;

        /// <summary>
        /// Initializes a new instance of the <see cref="FamilyReader"/> class.
        /// </summary>
        /// <param name="reader">The framed reader whose frames are validated.</param>
        /// <param name="channel">The channel whose family every read identifier must belong to.</param>
        public FamilyReader(ProtocolFrameReader reader, ProtocolChannel channel)
        {
            _reader = reader;
            _channel = channel;
        }

        protected override async ValueTask<ProtocolFrame?> ReadFrameCoreAsync(CancellationToken cancellationToken)
        {
            ProtocolFrame? frame = await _reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is { } value)
            {
                _channel.Validate(value.Type);
                _channel.ValidateCapability(value.Type);
            }
            return frame;
        }

        protected override ValueTask DisposeAsyncCore() => _reader.DisposeAsync();
    }

    private sealed class FamilyWriter : ProtocolFrameWriter
    {
        private readonly ProtocolFrameWriter _writer;
        private readonly ProtocolChannel _channel;

        /// <summary>
        /// Initializes a new instance of the <see cref="FamilyWriter"/> class.
        /// </summary>
        /// <param name="writer">The framed writer that receives validated frames.</param>
        /// <param name="channel">The channel whose family every written identifier must belong to.</param>
        public FamilyWriter(ProtocolFrameWriter writer, ProtocolChannel channel)
        {
            _writer = writer;
            _channel = channel;
        }

        protected override ValueTask WriteFrameCoreAsync(ProtocolFrame frame, CancellationToken cancellationToken)
        {
            _channel.Validate(frame.Type);
            _channel.ValidateCapability(frame.Type);
            return _writer.WriteFrameAsync(frame, cancellationToken);
        }

        protected override ValueTask FlushCoreAsync(CancellationToken cancellationToken) => _writer.FlushAsync(cancellationToken);
        protected override ValueTask DisposeAsyncCore() => _writer.DisposeAsync();
    }
}

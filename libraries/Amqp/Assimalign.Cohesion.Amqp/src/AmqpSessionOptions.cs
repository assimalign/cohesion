using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Configures a session: the flow-control limits and properties of its <c>begin</c> (AMQP 1.0 Part 2,
/// section 2.7.2).
/// </summary>
/// <remarks>
/// The session reads the options once, when it begins; changing them afterwards does not affect it.
/// </remarks>
[DebuggerDisplay("IncomingWindow = {IncomingWindow}, HandleMax = {HandleMax}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class AmqpSessionOptions
{
    private uint _incomingWindow = 2048;

    /// <summary>
    /// Initializes new session options with the defaults.
    /// </summary>
    public AmqpSessionOptions()
    {
    }

    /// <summary>
    /// Gets or sets how many <c>transfer</c> frames the session holds or lets the peer send before it grants
    /// more. Defaults to 2,048.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A frame keeps its place in the window until the application takes the delivery it belongs to, or until
    /// that delivery's link stops; a frame the session keeps nothing of, such as one of an aborted delivery,
    /// frees its place at once. The session restores the window with a <c>flow</c> once half of it is free, and
    /// ends with <see cref="AmqpErrorCondition.WindowViolation"/> when the peer sends past what it granted.
    /// </para>
    /// <para>
    /// The window is shared by every link on the session, so a receiver whose deliveries the application does not
    /// take holds back the others. The frames of a delivery still being reassembled hold no place, or a delivery
    /// larger than the window could never complete; each receiver's <see cref="AmqpLinkOptions.MaxMessageSize"/>
    /// bounds it instead. What the session buffers is therefore bounded by this value times the connection's
    /// maximum frame size, plus one delivery of up to its link's maximum message size for each receiver.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is zero.</exception>
    public uint IncomingWindow
    {
        get => _incomingWindow;
        set
        {
            ArgumentOutOfRangeException.ThrowIfZero(value);

            _incomingWindow = value;
        }
    }

    /// <summary>
    /// Gets or sets the highest link handle the peer may use on the session, which bounds how many links
    /// the peer can attach to it. Defaults to 1,023.
    /// </summary>
    /// <remarks>
    /// A peer that attaches a link with a larger handle closes the connection with
    /// <see cref="AmqpErrorCondition.FramingError"/>, as AMQP 1.0 Part 2, section 2.7.2 requires.
    /// </remarks>
    public uint HandleMax { get; set; } = 1023;

    /// <summary>
    /// Gets or sets the extension capabilities the local endpoint supports, or <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? OfferedCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the extension capabilities the local endpoint can use if the peer supports them, or
    /// <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? DesiredCapabilities { get; set; }

    /// <summary>
    /// Gets or sets the session properties the local endpoint sends in its <c>begin</c>, or <see langword="null"/>.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; set; }

    private sealed class DebugView
    {
        private readonly AmqpSessionOptions _options;

        public DebugView(AmqpSessionOptions options)
        {
            _options = options;
        }

        public uint IncomingWindow => _options.IncomingWindow;
        public uint HandleMax => _options.HandleMax;
        public IReadOnlyList<AmqpSymbol>? OfferedCapabilities => _options.OfferedCapabilities;
        public IReadOnlyList<AmqpSymbol>? DesiredCapabilities => _options.DesiredCapabilities;
        public IReadOnlyDictionary<AmqpSymbol, object?>? Properties => _options.Properties;
    }
}

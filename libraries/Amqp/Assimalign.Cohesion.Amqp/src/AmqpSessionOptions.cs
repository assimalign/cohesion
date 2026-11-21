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
    /// Gets or sets how many <c>transfer</c> frames the peer may send before the session grants more.
    /// Defaults to 2,048.
    /// </summary>
    /// <remarks>
    /// The session restores the window once half of it is used, as transfers arrive and whether or not the
    /// application has taken them, so the value bounds the frames in flight between two of the session's
    /// <c>flow</c> frames, not the frames in total, and not what the session holds: link credit and each
    /// receiver's <see cref="AmqpLinkOptions.MaxMessageSize"/> bound that.
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

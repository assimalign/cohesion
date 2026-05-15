namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

/// <summary>
/// A directional HTTP/2 flow-control window (RFC 9113 §5.2). A connection
/// has two — one for each direction — and every open stream has two more.
/// The window value is the number of octets the sender is permitted to
/// transmit; the receiver "credits" the sender with additional capacity
/// via <c>WINDOW_UPDATE</c> frames.
/// </summary>
/// <remarks>
/// <para>
/// Per RFC 9113 §6.9.1 the window value is a 31-bit unsigned quantity
/// capped at 2^31 - 1 (<see cref="MaxValue"/>). The struct stores it as
/// <see cref="long"/> so overflow checks during <see cref="TryReplenish"/>
/// can be performed in a single signed comparison without intermediate
/// promotion.
/// </para>
/// <para>
/// All operations are non-throwing; the caller decides whether a refused
/// increment should surface as a stream error (<c>RST_STREAM</c>) or a
/// connection error (<c>GOAWAY</c>).
/// </para>
/// </remarks>
internal struct Http2FlowControlWindow
{
    /// <summary>
    /// The maximum legal window value (2^31 - 1). A <c>WINDOW_UPDATE</c>
    /// frame that pushes the window above this value MUST be rejected as
    /// a <see cref="Http2ErrorCode.FlowControlError"/> per RFC 9113 §6.9.1.
    /// </summary>
    public const long MaxValue = int.MaxValue;

    private long _available;

    public Http2FlowControlWindow(long initialSize)
    {
        _available = initialSize;
    }

    /// <summary>The number of octets the sender may still transmit.</summary>
    public readonly long Available => _available;

    /// <summary>
    /// Atomically reserves <paramref name="bytes"/> octets from the window
    /// when there is sufficient capacity. Returns <see langword="false"/>
    /// when the window would underflow; the caller then surfaces that as a
    /// flow-control error.
    /// </summary>
    public bool TryConsume(long bytes)
    {
        if (bytes < 0)
        {
            return false;
        }

        if (_available < bytes)
        {
            return false;
        }

        _available -= bytes;
        return true;
    }

    /// <summary>
    /// Adds <paramref name="increment"/> octets back to the window in
    /// response to an inbound <c>WINDOW_UPDATE</c>. Returns
    /// <see langword="false"/> when the increment is non-positive (PROTOCOL
    /// or FLOW_CONTROL error depending on stream) or when the result would
    /// exceed <see cref="MaxValue"/> (FLOW_CONTROL_ERROR).
    /// </summary>
    public bool TryReplenish(int increment)
    {
        // RFC 9113 §6.9 — a WINDOW_UPDATE with a 0 increment is a
        // protocol error. Negative values are not legal on the wire (the
        // increment is a 31-bit unsigned quantity); reject defensively
        // in case the caller passed a signed value.
        if (increment <= 0)
        {
            return false;
        }

        long updated = _available + increment;
        if (updated > MaxValue)
        {
            return false;
        }

        _available = updated;
        return true;
    }

    /// <summary>
    /// Adjusts the window by the signed <paramref name="delta"/> in response
    /// to a <c>SETTINGS_INITIAL_WINDOW_SIZE</c> change (RFC 9113 §6.9.2).
    /// Returns <see langword="false"/> when the adjustment would push the
    /// window above <see cref="MaxValue"/> — that's a
    /// <see cref="Http2ErrorCode.FlowControlError"/>.
    /// </summary>
    /// <remarks>
    /// The window IS allowed to go negative when the delta is negative,
    /// per RFC 9113 §6.9.2. A negative window means the sender has
    /// already transmitted bytes "on credit"; the next
    /// <c>WINDOW_UPDATE</c> must bring it back to non-negative before
    /// further DATA is allowed.
    /// </remarks>
    public bool TryAdjustInitialWindow(int delta)
    {
        long updated = _available + delta;
        if (updated > MaxValue)
        {
            return false;
        }

        _available = updated;
        return true;
    }
}

using System;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// Per-connection abuse detector for the frame-rate HTTP/2 attack classes: rapid stream reset
/// (CVE-2023-44487), SETTINGS floods, and PING floods. Each detector is an independent
/// <see cref="Http2SlidingWindowCounter"/> sharing the operator-configured
/// <see cref="Http2ConnectionListenerOptions.Http2Limits.FloodDetectionWindow"/>.
/// </summary>
/// <remarks>
/// Owned by a single <see cref="Http2ConnectionContext"/> and driven only from its sequential
/// receive loop, so no synchronization is required. Each <c>Track…</c> method records one inbound
/// frame and returns <see langword="true"/> when that class of frame has exceeded its per-window
/// budget, at which point the caller terminates the connection with
/// <c>GOAWAY(ENHANCE_YOUR_CALM)</c>. AOT-safe — no reflection, no runtime codegen.
/// </remarks>
internal sealed class Http2FloodGuard
{
    private readonly Http2SlidingWindowCounter _resetStreams;
    private readonly Http2SlidingWindowCounter _settingsFrames;
    private readonly Http2SlidingWindowCounter _pingFrames;

    /// <summary>
    /// Creates a flood guard whose detectors are seeded from <paramref name="limits"/>.
    /// </summary>
    /// <param name="limits">The HTTP/2 abuse limits configured for the connection.</param>
    public Http2FloodGuard(Http2ConnectionListenerOptions.Http2Limits limits)
    {
        long windowMilliseconds = (long)limits.FloodDetectionWindow.TotalMilliseconds;
        _resetStreams = new Http2SlidingWindowCounter(limits.MaxResetStreamsPerWindow, windowMilliseconds);
        _settingsFrames = new Http2SlidingWindowCounter(limits.MaxSettingsFramesPerWindow, windowMilliseconds);
        _pingFrames = new Http2SlidingWindowCounter(limits.MaxPingFramesPerWindow, windowMilliseconds);
    }

    /// <summary>
    /// Records an inbound stream reset (a create-then-reset cycle) and returns whether the
    /// rapid-reset budget for the current window has been exceeded.
    /// </summary>
    public bool TrackStreamReset() => _resetStreams.RecordAndCheckExceeded(Environment.TickCount64);

    /// <summary>
    /// Records an inbound <c>SETTINGS</c> frame and returns whether the SETTINGS-flood budget for
    /// the current window has been exceeded.
    /// </summary>
    public bool TrackSettingsFrame() => _settingsFrames.RecordAndCheckExceeded(Environment.TickCount64);

    /// <summary>
    /// Records an inbound <c>PING</c> frame and returns whether the PING-flood budget for the
    /// current window has been exceeded.
    /// </summary>
    public bool TrackPingFrame() => _pingFrames.RecordAndCheckExceeded(Environment.TickCount64);
}

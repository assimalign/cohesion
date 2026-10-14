using System;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.Connections.Tcp.Internal;

/// <summary>
/// Paces a listener's retries after an accept fails for want of descriptors or buffers, and limits how
/// often the listener reports them.
/// </summary>
/// <remarks>
/// <para>
/// The wait starts at <see cref="InitialDelay"/> and doubles with each consecutive failure up to
/// <see cref="MaximumDelay"/>, the schedule Go's <c>net/http</c> server uses. The caller holds the current
/// delay for one accept, so the schedule starts over after every successful accept. Retrying at once
/// would spin for as long as the exhaustion lasts.
/// </para>
/// <para>
/// One instance belongs to one listener and limits its reports to one per <see cref="ReportInterval"/>, so
/// sustained or flapping exhaustion cannot flood a trace. A report carries the number of back-offs that
/// were not reported since the previous one.
/// </para>
/// </remarks>
internal sealed class TcpAcceptBackoff
{
    /// <summary>The first wait after an accept fails for want of resources.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(5);

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(1);

    /// <summary>The shortest time between two reports from one listener.</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();

    private bool _hasReported;
    private long _lastReportTimestamp;
    private int _unreported;

    /// <summary>
    /// Returns the wait that follows <paramref name="previousDelay"/>.
    /// </summary>
    /// <param name="previousDelay">The previous wait, or <see cref="TimeSpan.Zero"/> after an accept succeeded.</param>
    /// <returns><see cref="InitialDelay"/> for the first failure; otherwise twice the previous wait, at most <see cref="MaximumDelay"/>.</returns>
    public static TimeSpan NextDelay(TimeSpan previousDelay)
    {
        if (previousDelay <= TimeSpan.Zero)
        {
            return InitialDelay;
        }

        return previousDelay >= MaximumDelay / 2
            ? MaximumDelay
            : previousDelay * 2;
    }

    /// <summary>
    /// Records a back-off and decides whether to report it.
    /// </summary>
    /// <param name="timestamp">When the back-off began, as a <see cref="Stopwatch.GetTimestamp"/> value.</param>
    /// <param name="unreported">
    /// When this method returns <see langword="true"/>, the number of back-offs since the previous report that
    /// were not reported; otherwise zero.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when no back-off was reported in the <see cref="ReportInterval"/> before
    /// <paramref name="timestamp"/>; otherwise <see langword="false"/>, and the back-off is counted for the
    /// next report.
    /// </returns>
    public bool TryReport(long timestamp, out int unreported)
    {
        lock (_gate)
        {
            if (_hasReported && Stopwatch.GetElapsedTime(_lastReportTimestamp, timestamp) < ReportInterval)
            {
                _unreported = _unreported == int.MaxValue ? int.MaxValue : _unreported + 1;
                unreported = 0;

                return false;
            }

            _hasReported = true;
            _lastReportTimestamp = timestamp;
            unreported = _unreported;
            _unreported = 0;

            return true;
        }
    }
}

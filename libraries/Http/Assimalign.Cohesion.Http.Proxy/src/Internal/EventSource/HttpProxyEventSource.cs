using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;

namespace Assimalign.Cohesion.Http.Internal;

[EventSource(Name = "Assimalign.Cohesion.Http.Proxy")]
internal sealed class HttpProxyEventSource : EventSource
{
    // RFC 1035 §2.3.4 bounds a name at 253 characters; a destination is a name plus ":port".
    private const int maximumDestinationLength = 253 + 6;

    public static readonly HttpProxyEventSource Log = new();

    private long _destinationFailures;
    private long _currentTunnels;
    private long _nextFailureReport;
    private long _nextTooLargeReport;
    private int _suppressedFailures;
    private int _suppressedTooLarge;
    private int _streamingUnavailableReported;
    private PollingCounter? _currentTunnelsCounter;
    private IncrementingPollingCounter? _destinationFailuresCounter;

    private HttpProxyEventSource()
    {
    }

    internal long DestinationFailures => Interlocked.Read(ref _destinationFailures);

    internal long CurrentTunnels => Interlocked.Read(ref _currentTunnels);

    /// <summary>
    /// Reports an exchange or a tunnel the destination failed: it could not be reached, timed out,
    /// broke off, sent too large a response, or answered an upgrade the proxy could not verify.
    /// </summary>
    [NonEvent]
    public void DestinationFailed(HttpProxyResult result, string destination, Exception? exception)
    {
        Interlocked.Increment(ref _destinationFailures);

        if (IsEnabled(EventLevel.Warning, EventKeywords.None)
            && TryReport(ref _nextFailureReport, ref _suppressedFailures, out int suppressedCount))
        {
            DestinationFailed(result.ToString(), GetTypeName(exception), GetErrorCode(exception), suppressedCount);
        }

        WriteExchangeFailed(result, destination, exception);
    }

    /// <summary>
    /// Reports an exchange whose request body the client failed to send.
    /// </summary>
    [NonEvent]
    public void RequestBodyFailed(string destination, Exception? exception)
    {
        WriteExchangeFailed(HttpProxyResult.RequestBodyFailed, destination, exception);
    }

    /// <summary>
    /// Reports, once per process, that responses are buffered because the exchange carries no
    /// streaming feature.
    /// </summary>
    [NonEvent]
    public void ResponseBuffered()
    {
        if (IsEnabled(EventLevel.Warning, EventKeywords.None)
            && Interlocked.Exchange(ref _streamingUnavailableReported, 1) == 0)
        {
            ResponseStreamingUnavailable();
        }
    }

    /// <summary>
    /// Reports a buffered response whose body exceeded the configured limit.
    /// </summary>
    [NonEvent]
    public void ResponseTooLarge(long limit, string destination)
    {
        Interlocked.Increment(ref _destinationFailures);

        if (IsEnabled(EventLevel.Warning, EventKeywords.None)
            && TryReport(ref _nextTooLargeReport, ref _suppressedTooLarge, out int suppressedCount))
        {
            ResponseTooLarge(limit, suppressedCount);
        }

        WriteExchangeFailed(HttpProxyResult.ResponseTooLarge, destination, null);
    }

    /// <summary>
    /// Reports a tunnel the client accepted. Every call is paired with one <see cref="TunnelClosed"/>.
    /// </summary>
    [NonEvent]
    public void TunnelOpened(DnsEndPoint destination)
    {
        Interlocked.Increment(ref _currentTunnels);

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            TunnelOpened(FormatDestination(destination));
        }
    }

    /// <summary>
    /// Reports a tunnel that closed, with the bytes it carried each way.
    /// </summary>
    [NonEvent]
    public void TunnelClosed(DnsEndPoint destination, long bytesToDestination, long bytesToClient)
    {
        Interlocked.Decrement(ref _currentTunnels);

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            TunnelClosed(FormatDestination(destination), bytesToDestination, bytesToClient);
        }
    }

    // Deviates from event-source.md type-plus-message: socket and HttpClient messages name the
    // destination, which a forward proxy's client chooses (rule 11). The error code says why instead.
    [Event(1, Level = EventLevel.Warning, Message = "Forwarding failed with {0} ({1}, {2}); {3} earlier failures suppressed")]
    private void DestinationFailed(string result, string exceptionType, string errorCode, int suppressedCount)
        => WriteEvent(1, result, exceptionType, errorCode, suppressedCount);

    [Event(2, Level = EventLevel.Verbose, Message = "Exchange to {1} ended with {0} ({2}, {3})")]
    private void ExchangeFailed(string result, string destination, string exceptionType, string errorCode)
        => WriteEvent(2, result, destination, exceptionType, errorCode);

    [Event(3, Level = EventLevel.Warning, Message = "Responses are buffered: register HttpResponseStreamingInterceptor so the proxy can relay them as they arrive")]
    private void ResponseStreamingUnavailable()
        => WriteEvent(3);

    [Event(4, Level = EventLevel.Warning, Message = "A buffered response exceeded {0} bytes and was answered with 502; {1} earlier responses suppressed")]
    private void ResponseTooLarge(long limit, int suppressedCount)
        => WriteEvent(4, limit, suppressedCount);

    [Event(5, Level = EventLevel.Verbose, Message = "Tunnel to {0} opened")]
    private void TunnelOpened(string destination)
        => WriteEvent(5, destination);

    [Event(6, Level = EventLevel.Verbose, Message = "Tunnel to {0} closed after {1} bytes to the destination and {2} bytes to the client")]
    private void TunnelClosed(string destination, long bytesToDestination, long bytesToClient)
        => WriteEvent(6, destination, bytesToDestination, bytesToClient);

    [NonEvent]
    private void WriteExchangeFailed(HttpProxyResult result, string destination, Exception? exception)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ExchangeFailed(result.ToString(), Truncate(destination), GetTypeName(exception), GetErrorCode(exception));
        }
    }

    [NonEvent]
    private static string FormatDestination(DnsEndPoint destination)
    {
        return Truncate(destination.Host) + ":" + destination.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [NonEvent]
    private static string Truncate(string value)
    {
        return value.Length <= maximumDestinationLength ? value : value[..maximumDestinationLength];
    }

    [NonEvent]
    private static string GetTypeName(Exception? exception)
    {
        return exception is null ? string.Empty : exception.GetType().FullName ?? exception.GetType().Name;
    }

    // The most specific reason the platform reports, without the message that names the destination.
    [NonEvent]
    private static string GetErrorCode(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SocketException socket:
                    return socket.SocketErrorCode.ToString();
                case HttpRequestException request when request.HttpRequestError != HttpRequestError.Unknown:
                    return request.HttpRequestError.ToString();
                case HttpIOException io when io.HttpRequestError != HttpRequestError.Unknown:
                    return io.HttpRequestError.ToString();
            }
        }

        return string.Empty;
    }

    [NonEvent]
    private static bool TryReport(ref long nextReport, ref int suppressed, out int suppressedCount)
    {
        long now = Stopwatch.GetTimestamp();
        long next = Interlocked.Read(ref nextReport);
        if (now < next || Interlocked.CompareExchange(ref nextReport, now + Stopwatch.Frequency, next) != next)
        {
            Interlocked.Increment(ref suppressed);
            suppressedCount = 0;
            return false;
        }

        suppressedCount = Interlocked.Exchange(ref suppressed, 0);
        return true;
    }

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _currentTunnelsCounter ??= new PollingCounter("current-tunnels", this, () => CurrentTunnels)
            {
                DisplayName = "Open CONNECT tunnels",
            };
            _destinationFailuresCounter ??= new IncrementingPollingCounter("destination-failures", this, () => DestinationFailures)
            {
                DisplayName = "Destination failures",
                DisplayRateTimeScale = TimeSpan.FromSeconds(1),
            };
        }
    }
}

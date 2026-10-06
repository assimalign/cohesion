using System;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// The default server's process-wide instrumentation: the <see cref="ActivitySource"/> its request spans
/// come from and the <see cref="Meter"/> its HTTP server metrics come from, plus the OpenTelemetry HTTP
/// semantic-convention vocabulary both use.
/// </summary>
/// <remarks>
/// Both are named for this assembly, as an event source is (<c>.claude/rules/event-source.md</c>), so a
/// subscriber enables them by the one name <see cref="Name"/>. Only names are public; nothing outside
/// the server writes to them. The per-exchange work is <see cref="WebExchangeTelemetry"/>.
/// </remarks>
internal static class WebServerTelemetry
{
    /// <summary>
    /// The name of the <see cref="Source"/> and of the <see cref="Meter"/>: this assembly's name.
    /// </summary>
    public const string Name = "Assimalign.Cohesion.Web.Hosting";

    /// <summary>
    /// The OpenTelemetry environment variable that replaces the list of known HTTP methods.
    /// </summary>
    public const string KnownMethodsVariable = "OTEL_INSTRUMENTATION_HTTP_KNOWN_METHODS";

    /// <summary>The value <c>http.request.method</c> takes for a method outside the known list.</summary>
    public const string OtherMethod = "_OTHER";

    /// <summary>The span name prefix for a method outside the known list.</summary>
    public const string OtherMethodSpanName = "HTTP";

    /// <summary>
    /// <c>error.type</c> for an exchange cancelled before its response was delivered: the peer reset the
    /// stream or closed the connection, the server stopped, or the application called
    /// <see cref="IHttpContext.Cancel"/>.
    /// </summary>
    public const string RequestCanceled = "request_canceled";

    /// <summary>
    /// <c>error.type</c> for a pipeline that threw when its response could no longer be replaced with a
    /// <c>500</c>: the response had started, or could not be reshaped.
    /// </summary>
    public const string UnhandledException = "unhandled_exception";

    /// <summary>
    /// <c>error.type</c> for a response that could not be put on the wire.
    /// </summary>
    public const string ResponseSendFailed = "response_send_failed";

    /// <summary>
    /// The source of the server's request spans.
    /// </summary>
    public static readonly ActivitySource Source = new(Name);

    /// <summary>
    /// The meter of the server's HTTP metrics.
    /// </summary>
    public static readonly Meter Meter = new(Name);

    /// <summary>
    /// <c>http.server.request.duration</c>: the duration of each exchange, in seconds, with the
    /// semantic convention's bucket boundaries as advice.
    /// </summary>
    public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "http.server.request.duration",
        unit: "s",
        description: "Duration of HTTP server requests.",
        tags: null,
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10],
        });

    /// <summary>
    /// <c>http.server.active_requests</c>: the number of exchanges in flight.
    /// </summary>
    public static readonly UpDownCounter<long> ActiveRequests = Meter.CreateUpDownCounter<long>(
        "http.server.active_requests",
        unit: "{request}",
        description: "Number of active HTTP server requests.");

    // The semantic convention requires a way to override the known methods, because a method outside
    // them is reported as _OTHER. Read once: the instruments are process-wide too.
    private static readonly FrozenSet<string>? _knownMethods = ParseKnownMethods(
        Environment.GetEnvironmentVariable(KnownMethodsVariable));

    // Status codes recur, so their boxed and text forms are cached rather than allocated per exchange.
    private static readonly object?[] _boxedStatusCodes = new object?[600];
    private static readonly string?[] _statusCodeTexts = new string?[600];

    /// <summary>
    /// Maps a request method to its <c>http.request.method</c> value: the method itself when it is
    /// known, and <see cref="OtherMethod"/> otherwise.
    /// </summary>
    /// <param name="method">The request method.</param>
    /// <param name="isKnown">Whether the method is known.</param>
    /// <returns>The attribute value.</returns>
    public static string GetMethodAttribute(HttpMethod method, out bool isKnown)
    {
        return GetMethodAttribute(method, _knownMethods, out isKnown);
    }

    /// <summary>
    /// Maps a request method to its <c>http.request.method</c> value against an explicit known-method
    /// list, or the semantic convention's default list when <paramref name="knownMethods"/> is
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="method">The request method.</param>
    /// <param name="knownMethods">The override list, or <see langword="null"/> for the default list.</param>
    /// <param name="isKnown">Whether the method is known.</param>
    /// <returns>The attribute value.</returns>
    public static string GetMethodAttribute(HttpMethod method, FrozenSet<string>? knownMethods, out bool isKnown)
    {
        string value = method.Value ?? string.Empty;

        // The default list is the semantic convention's: RFC 9110's eight methods, PATCH (RFC 5789) and
        // QUERY. They are also exactly the methods HttpMethod canonicalizes.
        isKnown = knownMethods?.Contains(value) ?? value is
            "GET" or "HEAD" or "POST" or "PUT" or "DELETE" or "CONNECT" or "OPTIONS" or "TRACE" or "PATCH" or "QUERY";

        return isKnown ? value : OtherMethod;
    }

    /// <summary>
    /// Parses the <see cref="KnownMethodsVariable"/> value: a comma-separated, case-sensitive list that
    /// replaces the default list. Returns <see langword="null"/> (the default list) when the value is
    /// unset or blank.
    /// </summary>
    /// <param name="value">The variable's value.</param>
    /// <returns>The known methods, or <see langword="null"/> for the default list.</returns>
    public static FrozenSet<string>? ParseKnownMethods(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the <c>url.scheme</c> value, or <see langword="null"/> when the transport reported none.
    /// </summary>
    /// <param name="scheme">The request scheme.</param>
    /// <returns>The attribute value, or <see langword="null"/>.</returns>
    public static string? GetScheme(HttpScheme scheme) => scheme switch
    {
        HttpScheme.Http => "http",
        HttpScheme.Https => "https",
        _ => null,
    };

    /// <summary>
    /// Gets the <c>network.protocol.version</c> value, or <see langword="null"/> when the version is
    /// unknown.
    /// </summary>
    /// <param name="version">The exchange's HTTP version.</param>
    /// <returns>The attribute value, or <see langword="null"/>.</returns>
    public static string? GetProtocolVersion(HttpVersion version) => version switch
    {
        HttpVersion.Http11 => "1.1",
        HttpVersion.Http20 => "2",
        HttpVersion.Http30 => "3",
        _ => null,
    };

    /// <summary>
    /// Gets the boxed form of a status code, the <c>http.response.status_code</c> attribute value.
    /// </summary>
    /// <param name="statusCode">The status code.</param>
    /// <returns>The boxed status code.</returns>
    public static object GetBoxedStatusCode(int statusCode)
    {
        if ((uint)statusCode < (uint)_boxedStatusCodes.Length)
        {
            // A benign race: two threads may each box the same value once.
            return _boxedStatusCodes[statusCode] ??= statusCode;
        }

        return statusCode;
    }

    /// <summary>
    /// Gets the text form of a status code, the <c>error.type</c> value of a server error response.
    /// </summary>
    /// <param name="statusCode">The status code.</param>
    /// <returns>The status code as a decimal string.</returns>
    public static string GetStatusCodeText(int statusCode)
    {
        if ((uint)statusCode < (uint)_statusCodeTexts.Length)
        {
            return _statusCodeTexts[statusCode] ??= statusCode.ToString(CultureInfo.InvariantCulture);
        }

        return statusCode.ToString(CultureInfo.InvariantCulture);
    }
}

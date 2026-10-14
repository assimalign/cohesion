using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Internal;

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
    public const string Name = "Assimalign.Cohesion.Web.Server";

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
    /// <c>error.type</c> for a pipeline that threw when its response could no longer be replaced, after
    /// the transport reported that the client's request was at fault (<see cref="IWebClientFaultFeature"/>):
    /// reading its body failed on the client's side once the response had started, so the transport's
    /// own status could not replace it and the exchange was reset. The application did not fail, so it is
    /// not <see cref="UnhandledException"/>. It also names an exchange cancelled after such a report once its
    /// response had started: on HTTP/2 and HTTP/3 the transport's answer or reset for the fault fires the
    /// exchange's cancellation, so the client's fault, not a cancellation of its own, is how it ended (#1378).
    /// Before the response started, the transport's own status is the outcome and no error is reported.
    /// </summary>
    public const string ClientFault = "client_fault";

    /// <summary>
    /// <c>error.type</c> for a response that could not be put on the wire.
    /// </summary>
    public const string ResponseSendFailed = "response_send_failed";

    /// <summary>
    /// The text that replaces a query parameter's value in <c>url.query</c>: the redaction marker the
    /// semantic convention uses.
    /// </summary>
    public const string RedactedQueryValue = "REDACTED";

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

    // A traceparent's version-00 fields: version "-" trace-id "-" parent-id "-" trace-flags, which is
    // 2 + 1 + 32 + 1 + 16 + 1 + 2 characters.
    private const int traceParentLength = 55;

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
    /// Gets the <c>url.query</c> value: the request's query parameters with every value replaced by
    /// <see cref="RedactedQueryValue"/>, except those of the parameters the application declared safe to
    /// show. Returns <see langword="null"/> when the request carried no query parameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A query string carries tokens, signatures and personal data that the server cannot recognize, so
    /// a value is shown only when the application named its parameter. The names stay, so the span still
    /// shows the query's shape. A parameter without a value is written as its name alone: there is
    /// nothing to hide, and writing <see cref="RedactedQueryValue"/> would claim a value was sent.
    /// </para>
    /// <para>
    /// The value is rebuilt from the parsed query, the only form the request carries, so it is
    /// normalized: names and shown values are percent-encoded again, a parameter with an empty name is
    /// left out, and a repeated name appears once, as the collection holds it.
    /// </para>
    /// </remarks>
    /// <param name="query">The request's parsed query.</param>
    /// <param name="shownValues">
    /// The parameters whose values are shown, compared by the set's comparer, or <see langword="null"/>
    /// to redact every value.
    /// </param>
    /// <returns>The attribute value, or <see langword="null"/>.</returns>
    public static string? GetQueryAttribute(IHttpQueryCollection query, FrozenSet<string>? shownValues)
    {
        if (query.Count == 0)
        {
            return null;
        }

        DefaultInterpolatedStringHandler builder = new(0, 0, CultureInfo.InvariantCulture, stackalloc char[256]);
        bool first = true;

        foreach (KeyValuePair<HttpQueryKey, HttpQueryValue> parameter in query)
        {
            if (!first)
            {
                builder.AppendLiteral("&");
            }

            first = false;

            string name = parameter.Key.Value;
            string value = parameter.Value.Value;

            builder.AppendFormatted(Uri.EscapeDataString(name));

            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            builder.AppendLiteral("=");
            builder.AppendFormatted(shownValues is not null && shownValues.Contains(name)
                ? Uri.EscapeDataString(value)
                : RedactedQueryValue);
        }

        return builder.ToStringAndClear();
    }

    /// <summary>
    /// Parses a W3C <c>traceparent</c> value into the caller's remote context, reading a later
    /// version's header by its version-00 fields (W3C Trace Context §3.2.4, "Versioning of
    /// traceparent").
    /// </summary>
    /// <remarks>
    /// <para>
    /// A version-00 value is exactly <c>version "-" trace-id "-" parent-id "-" trace-flags</c>, 55
    /// characters. A later version may append fields, each after a dash, and a parser that does not know
    /// the version reads the version-00 fields and ignores the rest: the flags must be followed by the
    /// end of the value or a dash. Version <c>ff</c> is invalid, as are upper-case hex, an all-zero
    /// trace or parent id, and a separator other than a dash.
    /// </para>
    /// <para>
    /// <see cref="ActivityContext.TryParse(string, string, bool, out ActivityContext)"/> accepts only
    /// 55 characters, so it rejected every later-version header with extra fields and the request
    /// started a new trace (#1299); it also does not check the separators. This method checks the
    /// separators and the length, and the BCL parser checks the rest. Of a later version's flags only
    /// the sampled bit is kept: the specification defines the other bits for version 00 only.
    /// </para>
    /// </remarks>
    /// <param name="value">The <c>traceparent</c> field value.</param>
    /// <param name="traceState">The combined <c>tracestate</c> list, or <see langword="null"/>.</param>
    /// <param name="context">The caller's context when the value is valid.</param>
    /// <returns><see langword="true"/> when the value is a valid <c>traceparent</c>.</returns>
    public static bool TryParseTraceParent(string? value, string? traceState, out ActivityContext context)
    {
        context = default;

        if (value is null || value.Length < traceParentLength || value[2] != '-' || value[35] != '-' || value[52] != '-')
        {
            return false;
        }

        bool laterVersion = value[0] != '0' || value[1] != '0';

        if (value.Length > traceParentLength)
        {
            // Version 00 has no further fields; a later version separates each one with a dash.
            if (!laterVersion || value[traceParentLength] != '-')
            {
                return false;
            }

            value = value[..traceParentLength];
        }

        // Checks a lower-case hex version other than ff, lower-case hex ids that are not all zeros, and
        // lower-case hex flags.
        if (!ActivityContext.TryParse(value, traceState, isRemote: true, out context))
        {
            return false;
        }

        if (laterVersion)
        {
            context = new ActivityContext(
                context.TraceId,
                context.SpanId,
                context.TraceFlags & ActivityTraceFlags.Recorded,
                context.TraceState,
                isRemote: true);
        }

        return true;
    }

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

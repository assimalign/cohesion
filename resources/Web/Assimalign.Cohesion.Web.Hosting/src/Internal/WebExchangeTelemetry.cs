using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// One exchange's telemetry: its server span, its <c>http.server.request.duration</c> and
/// <c>http.server.active_requests</c> measurements, and its request id.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WebApplicationServer"/> starts it before the exchange's pipeline runs, reports how the
/// exchange was finalized, and stops it after the response was delivered and its completion callbacks
/// ran, before the exchange is disposed. The span and the duration therefore cover the whole exchange,
/// the response included. While the pipeline runs, the span is <see cref="Activity.Current"/>.
/// </para>
/// <para>
/// Instrumentation costs nothing when nobody listens. <see cref="Start"/> checks
/// <see cref="ActivitySource.HasListeners"/> and each instrument's <see cref="Instrument.Enabled"/>;
/// with all three off it creates no activity, reads no request state and records nothing, and
/// <see cref="Stop"/> returns at once. What remains is this object, installed as the exchange's
/// <see cref="IWebRequestIdFeature"/>.
/// </para>
/// <para>
/// Listener callbacks run inline, inside these calls. A callback that throws costs this exchange its
/// telemetry, never the exchange: <see cref="Start"/> and <see cref="Stop"/> contain the failure.
/// </para>
/// </remarks>
internal sealed class WebExchangeTelemetry : IWebRequestIdFeature
{
    private const string httpRequestMethod = "http.request.method";
    private const string httpRequestMethodOriginal = "http.request.method_original";
    private const string httpResponseStatusCode = "http.response.status_code";
    private const string httpRoute = "http.route";
    private const string urlPath = "url.path";
    private const string urlScheme = "url.scheme";
    private const string serverAddress = "server.address";
    private const string serverPort = "server.port";
    private const string networkProtocolVersion = "network.protocol.version";
    private const string errorType = "error.type";

    private readonly IHttpContext _exchange;

    // Set by Start when any instrument was on. A null method means the exchange is not instrumented.
    private string? _method;
    private string? _scheme;
    private string? _spanName;
    private Activity? _activity;
    private long _startTimestamp;
    private bool _activeRequestRecorded;
    private ExchangeResult _result;

    private StrongBox<ActivityTraceId>? _requestId;

    private WebExchangeTelemetry(IHttpContext exchange)
    {
        _exchange = exchange;
    }

    /// <inheritdoc />
    public string Name => nameof(IWebRequestIdFeature);

    /// <inheritdoc />
    public ActivityTraceId RequestId
    {
        get
        {
            // A process that forces hierarchical activity ids gets spans without a W3C trace id.
            if (_activity is { IdFormat: ActivityIdFormat.W3C } activity)
            {
                return activity.TraceId;
            }

            StrongBox<ActivityTraceId>? requestId = Volatile.Read(ref _requestId);

            if (requestId is null)
            {
                // Two readers can race to resolve a random id; exactly one is published.
                Interlocked.CompareExchange(ref _requestId, new StrongBox<ActivityTraceId>(ResolveRequestId()), null);
                requestId = Volatile.Read(ref _requestId)!;
            }

            return requestId.Value;
        }
    }

    /// <summary>
    /// Starts the exchange's telemetry and installs it as the exchange's <see cref="IWebRequestIdFeature"/>.
    /// </summary>
    /// <param name="exchange">The exchange about to run its pipeline.</param>
    /// <returns>The exchange's telemetry, which the server stops once the exchange is finalized.</returns>
    public static WebExchangeTelemetry Start(IHttpContext exchange)
    {
        WebExchangeTelemetry telemetry = new(exchange);

        bool traced = WebServerTelemetry.Source.HasListeners();
        bool timed = WebServerTelemetry.RequestDuration.Enabled;
        bool counted = WebServerTelemetry.ActiveRequests.Enabled;

        try
        {
            exchange.Features.Set(telemetry);

            if (traced || timed || counted)
            {
                telemetry.Instrument(traced, timed, counted);
            }
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: listener callbacks
        // are application code that runs inline. Their failure costs this exchange its telemetry and
        // nothing else (docs/DESIGN.md, "Server telemetry").
        catch (Exception)
        {
        }

        return telemetry;
    }

    /// <summary>
    /// Records how the server finalized the exchange.
    /// </summary>
    /// <param name="responded">Whether a response was sent; <see langword="false"/> when the exchange was reset.</param>
    /// <param name="canceled">Whether the pipeline ended because the exchange or the server was cancelled.</param>
    public void SetOutcome(bool responded, bool canceled)
    {
        _result = responded
            ? ExchangeResult.Responded
            : canceled ? ExchangeResult.Canceled : ExchangeResult.Faulted;
    }

    /// <summary>
    /// Ends the exchange's telemetry: records its duration and the end of its active request, and
    /// tags and stops its span.
    /// </summary>
    /// <remarks>
    /// An exchange that never reported an outcome failed while its response was being sent.
    /// </remarks>
    public void Stop()
    {
        if (_method is null)
        {
            return;
        }

        int statusCode = 0;
        string? error = null;
        string? route = null;
        TimeSpan elapsed = TimeSpan.Zero;

        try
        {
            if (_startTimestamp != 0)
            {
                elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
            }

            error = _result switch
            {
                ExchangeResult.Responded => null,
                ExchangeResult.Canceled => WebServerTelemetry.RequestCanceled,
                ExchangeResult.Faulted => WebServerTelemetry.UnhandledException,
                _ => WebServerTelemetry.ResponseSendFailed,
            };

            // A status was sent when the response was, and when the application had already started a
            // streamed response before the exchange was reset. A failed send leaves it unknown: the
            // transport marks the head committed before it writes it, so the status is not reported.
            if (_result == ExchangeResult.Responded
                || ((_result is ExchangeResult.Canceled or ExchangeResult.Faulted) && _exchange.HasResponseStarted))
            {
                statusCode = _exchange.Response.StatusCode.Value;

                if (error is null && statusCode >= 500)
                {
                    error = WebServerTelemetry.GetStatusCodeText(statusCode);
                }
            }

            if (_startTimestamp != 0 || _activity is not null)
            {
                route = _exchange.Features.Get<IWebEndpointFeature>()?.RouteTemplate;
            }

            RecordMetrics(statusCode, error, route, elapsed);
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: see Start.
        catch (Exception)
        {
        }
        finally
        {
            StopActivity(statusCode, error, route, elapsed);
        }
    }

    private void Instrument(bool traced, bool timed, bool counted)
    {
        IHttpRequest request = _exchange.Request;
        string method = WebServerTelemetry.GetMethodAttribute(request.Method, out bool known);

        _method = method;
        _scheme = WebServerTelemetry.GetScheme(request.Scheme);

        if (counted)
        {
            WebServerTelemetry.ActiveRequests.Add(1, CreateRequestTags());
            _activeRequestRecorded = true;
        }

        if (timed)
        {
            // Taken before the span starts, so the span can end at its start plus the measured duration
            // and report the same value as the metric.
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        if (traced)
        {
            _spanName = known ? method : WebServerTelemetry.OtherMethodSpanName;
            _activity = StartActivity(request, known);
        }
    }

    private Activity? StartActivity(IHttpRequest request, bool knownMethod)
    {
        TryGetRemoteParent(request.Headers, includeTraceState: true, out ActivityContext parent);

        // A server span's parent is the remote caller, or nothing. The accept loop inherits whatever
        // activity was current when the server started, and that activity is no request's parent.
        if (Activity.Current is not null)
        {
            Activity.Current = null;
        }

        // The attributes the semantic convention asks for at creation, so samplers can read them.
        TagList tags = default;
        tags.Add(httpRequestMethod, _method);

        if (!knownMethod)
        {
            tags.Add(httpRequestMethodOriginal, request.Method.Value);
        }

        tags.Add(urlPath, request.Path.Value ?? "/");

        if (_scheme is not null)
        {
            tags.Add(urlScheme, _scheme);
        }

        HttpHost host = request.Host;

        if (!host.IsEmpty)
        {
            tags.Add(serverAddress, host.Host);

            if (host.Port is int port)
            {
                tags.Add(serverPort, port);
            }
        }

        if (WebServerTelemetry.GetProtocolVersion(_exchange.Version) is { } version)
        {
            tags.Add(networkProtocolVersion, version);
        }

        return WebServerTelemetry.Source.StartActivity(_spanName!, ActivityKind.Server, parent, tags);
    }

    private void RecordMetrics(int statusCode, string? error, string? route, TimeSpan elapsed)
    {
        if (_activeRequestRecorded)
        {
            WebServerTelemetry.ActiveRequests.Add(-1, CreateRequestTags());
        }

        if (_startTimestamp == 0)
        {
            return;
        }

        TagList tags = CreateRequestTags();

        if (statusCode != 0)
        {
            tags.Add(httpResponseStatusCode, WebServerTelemetry.GetBoxedStatusCode(statusCode));
        }

        if (route is not null)
        {
            tags.Add(httpRoute, route);
        }

        if (error is not null)
        {
            tags.Add(errorType, error);
        }

        if (WebServerTelemetry.GetProtocolVersion(_exchange.Version) is { } version)
        {
            tags.Add(networkProtocolVersion, version);
        }

        WebServerTelemetry.RequestDuration.Record(elapsed.TotalSeconds, tags);
    }

    private void StopActivity(int statusCode, string? error, string? route, TimeSpan elapsed)
    {
        if (_activity is not { } activity)
        {
            return;
        }

        try
        {
            if (statusCode != 0)
            {
                activity.SetTag(httpResponseStatusCode, WebServerTelemetry.GetBoxedStatusCode(statusCode));
            }

            if (route is not null)
            {
                activity.SetTag(httpRoute, route);
                activity.DisplayName = string.Concat(_spanName, " ", route);
            }

            if (error is not null)
            {
                activity.SetTag(errorType, error);
                activity.SetStatus(ActivityStatusCode.Error);
            }

            if (elapsed > TimeSpan.Zero)
            {
                activity.SetEndTime(activity.StartTimeUtc + elapsed);
            }

            activity.Stop();
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: see Start.
        catch (Exception)
        {
        }
    }

    // http.server.active_requests carries only these two attributes; the duration starts from them.
    private TagList CreateRequestTags()
    {
        TagList tags = default;
        tags.Add(httpRequestMethod, _method);

        if (_scheme is not null)
        {
            tags.Add(urlScheme, _scheme);
        }

        return tags;
    }

    private ActivityTraceId ResolveRequestId()
    {
        return TryGetRemoteParent(_exchange.Request.Headers, includeTraceState: false, out ActivityContext parent)
            ? parent.TraceId
            : ActivityTraceId.CreateRandom();
    }

    // W3C Trace Context: a request carries exactly one traceparent field, and its tracestate fields
    // combine into one list. A missing, repeated or malformed traceparent leaves no parent.
    private static bool TryGetRemoteParent(IHttpHeaderCollection headers, bool includeTraceState, out ActivityContext parent)
    {
        if (headers.TryGetValue(HttpHeaderKey.TraceParent, out HttpHeaderValue traceParent) && traceParent.Count == 1)
        {
            string? traceState = includeTraceState
                && headers.TryGetValue(HttpHeaderKey.TraceState, out HttpHeaderValue state)
                && !state.IsEmpty
                    ? state.Value
                    : null;

            return ActivityContext.TryParse(traceParent[0], traceState, isRemote: true, out parent);
        }

        parent = default;
        return false;
    }

    /// <summary>
    /// How the server finalized the exchange.
    /// </summary>
    private enum ExchangeResult
    {
        /// <summary>No outcome was reported: sending the response failed.</summary>
        SendFailed,

        /// <summary>A response was sent.</summary>
        Responded,

        /// <summary>The exchange was cancelled and reset.</summary>
        Canceled,

        /// <summary>The pipeline threw, its response could not be replaced, and the exchange was reset.</summary>
        Faulted,
    }
}

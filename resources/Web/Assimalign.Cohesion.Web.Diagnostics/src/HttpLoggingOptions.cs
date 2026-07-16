using System;
using System.Collections.Generic;
using System.Net;

namespace Assimalign.Cohesion.Web.Diagnostics;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Logging;

/// <summary>
/// Builder-time configuration for the HTTP logging middleware
/// (<see cref="WebApplicationExtensions.UseHttpLogging(IWebApplicationPipelineBuilder, ILogger, Action{HttpLoggingOptions})"/>).
/// </summary>
/// <remarks>
/// <para>
/// The options are read once when the middleware is composed and frozen into an immutable
/// snapshot — mutating an options instance after <c>UseHttpLogging</c> returns has no effect.
/// There is no request-time configuration surface.
/// </para>
/// <para>
/// <b>Redaction model.</b> Redaction is allowlist-based: a header's <em>name</em> always logs,
/// but its <em>value</em> logs only when the name is present in
/// <see cref="AllowedRequestHeaders"/> / <see cref="AllowedResponseHeaders"/>; every other value
/// is replaced with <see cref="RedactedValue"/>. Credential-bearing headers —
/// <c>Authorization</c>, <c>Proxy-Authorization</c>, <c>Cookie</c>, and <c>Set-Cookie</c> — are
/// deliberately absent from the default allowlists, so their values are never logged unless a
/// caller explicitly adds them.
/// </para>
/// </remarks>
public sealed class HttpLoggingOptions
{
    /// <summary>
    /// Gets or sets the parts of the exchange to capture and emit. Defaults to
    /// <see cref="HttpLoggingFields.Default"/> (bodies and query string excluded).
    /// </summary>
    public HttpLoggingFields Fields { get; set; } = HttpLoggingFields.Default;

    /// <summary>
    /// Gets or sets the category the middleware logs under. Defaults to
    /// <c>Assimalign.Cohesion.Web.Diagnostics.HttpLogging</c>. Use it with
    /// <see cref="LoggerFilterRule"/> to route access-log entries to (or away from) specific
    /// providers.
    /// </summary>
    public string Category { get; set; } = "Assimalign.Cohesion.Web.Diagnostics.HttpLogging";

    /// <summary>
    /// Gets or sets the level exchange entries are emitted at. Defaults to
    /// <see cref="LogLevel.Information"/>. When the composed logger reports the level disabled at
    /// request time, the middleware is a pure pass-through — nothing is captured or timed. An
    /// exchange whose downstream middleware throws is escalated to <see cref="LogLevel.Error"/>.
    /// </summary>
    public LogLevel Level { get; set; } = LogLevel.Information;

    /// <summary>
    /// Gets or sets whether a request-start entry is emitted in addition to the completion
    /// entry. Defaults to <see langword="false"/>. When enabled, the start entry seeds an
    /// <see cref="IScopedLogger"/> scope and the completion entry is written through it, so the
    /// two correlate via <see cref="ILoggerEntry.ParentId"/>.
    /// </summary>
    public bool LogRequestStart { get; set; }

    /// <summary>
    /// Gets the request headers whose values may be logged. Values of headers not in this set
    /// are replaced with <see cref="RedactedValue"/>. The default set carries common
    /// content-negotiation, caching, and user-agent headers; <c>Authorization</c>,
    /// <c>Proxy-Authorization</c>, and <c>Cookie</c> are never included by default.
    /// </summary>
    public ISet<HttpHeaderKey> AllowedRequestHeaders { get; } = new HashSet<HttpHeaderKey>
    {
        HttpHeaderKey.Accept,
        HttpHeaderKey.AcceptCharset,
        HttpHeaderKey.AcceptEncoding,
        HttpHeaderKey.AcceptLanguage,
        HttpHeaderKey.CacheControl,
        HttpHeaderKey.Connection,
        HttpHeaderKey.ContentEncoding,
        HttpHeaderKey.ContentLength,
        HttpHeaderKey.ContentType,
        HttpHeaderKey.Expect,
        HttpHeaderKey.Host,
        HttpHeaderKey.MaxForwards,
        HttpHeaderKey.Origin,
        HttpHeaderKey.Pragma,
        HttpHeaderKey.Range,
        HttpHeaderKey.Referer,
        HttpHeaderKey.TE,
        HttpHeaderKey.Trailer,
        HttpHeaderKey.TransferEncoding,
        HttpHeaderKey.Upgrade,
        HttpHeaderKey.UserAgent,
        HttpHeaderKey.Via,
    };

    /// <summary>
    /// Gets the response headers whose values may be logged. Values of headers not in this set
    /// are replaced with <see cref="RedactedValue"/>. <c>Set-Cookie</c> is never included by
    /// default.
    /// </summary>
    public ISet<HttpHeaderKey> AllowedResponseHeaders { get; } = new HashSet<HttpHeaderKey>
    {
        HttpHeaderKey.AcceptRanges,
        HttpHeaderKey.Age,
        HttpHeaderKey.Allow,
        HttpHeaderKey.AltSvc,
        HttpHeaderKey.CacheControl,
        HttpHeaderKey.Connection,
        HttpHeaderKey.ContentDisposition,
        HttpHeaderKey.ContentEncoding,
        HttpHeaderKey.ContentLanguage,
        HttpHeaderKey.ContentLength,
        HttpHeaderKey.ContentLocation,
        HttpHeaderKey.ContentRange,
        HttpHeaderKey.ContentType,
        HttpHeaderKey.Date,
        HttpHeaderKey.Expires,
        HttpHeaderKey.LastModified,
        HttpHeaderKey.Location,
        HttpHeaderKey.RetryAfter,
        HttpHeaderKey.Server,
        HttpHeaderKey.TransferEncoding,
        HttpHeaderKey.Upgrade,
        HttpHeaderKey.Vary,
    };

    /// <summary>
    /// Gets or sets the placeholder written in place of a redacted header value. Defaults to
    /// <c>[Redacted]</c>.
    /// </summary>
    public string RedactedValue { get; set; } = "[Redacted]";

    /// <summary>
    /// Gets or sets the maximum number of request-body bytes captured when
    /// <see cref="HttpLoggingFields.RequestBody"/> is enabled. Defaults to 4096. The cap bounds
    /// only what is <em>captured for logging</em> — the body itself streams through untouched,
    /// so transport backpressure and request-size limits are unaffected.
    /// </summary>
    public int RequestBodyLimit { get; set; } = 4096;

    /// <summary>
    /// Gets or sets the maximum number of response-body bytes captured when
    /// <see cref="HttpLoggingFields.ResponseBody"/> is enabled. Defaults to 4096.
    /// </summary>
    public int ResponseBodyLimit { get; set; } = 4096;

    /// <summary>
    /// Gets the content types eligible for body capture. An entry matches when the message's
    /// media type (the <c>Content-Type</c> value before any parameters) starts with the entry
    /// (ordinal, case-insensitive), or — for entries beginning with <c>+</c> — when the media
    /// type ends with that structured-syntax suffix (covering e.g. <c>application/problem+json</c>).
    /// Bodies whose content type matches no entry are not captured.
    /// </summary>
    public IList<string> LoggableBodyContentTypes { get; } = new List<string>
    {
        "application/json",
        "application/xml",
        "application/x-www-form-urlencoded",
        "text/",
        "+json",
        "+xml",
    };

    /// <summary>
    /// Gets or sets the resolver for the effective client address logged under
    /// <see cref="HttpLoggingAttributes.ClientAddress"/>. When <see langword="null"/> (the
    /// default) the transport socket peer (<see cref="IHttpConnectionInfo.RemoteIp"/>) is
    /// logged.
    /// </summary>
    /// <remarks>
    /// This is the composition seam for proxy awareness: once the forwarded-headers middleware
    /// (issue #778) establishes a trusted client address on the exchange, plug a resolver here
    /// that reads it. Until then the socket peer is the only honest answer — the middleware
    /// never trusts <c>X-Forwarded-For</c> on its own.
    /// </remarks>
    public Func<IHttpContext, IPAddress?>? ClientAddressResolver { get; set; }

    /// <summary>
    /// Gets or sets the time source used for duration measurement and the request-start
    /// timestamp. Defaults to <see cref="TimeProvider.System"/>; substitute a fake in tests to
    /// make durations deterministic.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

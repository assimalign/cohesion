using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Net;

namespace Assimalign.Cohesion.Web.Diagnostics.Internal;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Logging;

/// <summary>
/// The immutable middleware-side view of <see cref="HttpLoggingOptions"/>, frozen when
/// <c>UseHttpLogging</c> composes the pipeline. Mutations to the options object after
/// composition are invisible by design — there is no request-time configuration.
/// </summary>
internal sealed class HttpLoggingSnapshot
{
    private HttpLoggingSnapshot(
        HttpLoggingFields fields,
        string category,
        LogLevel level,
        bool logRequestStart,
        FrozenSet<HttpHeaderKey> allowedRequestHeaders,
        FrozenSet<HttpHeaderKey> allowedResponseHeaders,
        string redactedValue,
        int requestBodyLimit,
        int responseBodyLimit,
        string[] loggableBodyContentTypes,
        Func<IHttpContext, IPAddress?>? clientAddressResolver,
        TimeProvider timeProvider)
    {
        Fields = fields;
        Category = category;
        Level = level;
        LogRequestStart = logRequestStart;
        AllowedRequestHeaders = allowedRequestHeaders;
        AllowedResponseHeaders = allowedResponseHeaders;
        RedactedValue = redactedValue;
        RequestBodyLimit = requestBodyLimit;
        ResponseBodyLimit = responseBodyLimit;
        LoggableBodyContentTypes = loggableBodyContentTypes;
        ClientAddressResolver = clientAddressResolver;
        TimeProvider = timeProvider;
    }

    public HttpLoggingFields Fields { get; }
    public string Category { get; }
    public LogLevel Level { get; }
    public bool LogRequestStart { get; }
    public FrozenSet<HttpHeaderKey> AllowedRequestHeaders { get; }
    public FrozenSet<HttpHeaderKey> AllowedResponseHeaders { get; }
    public string RedactedValue { get; }
    public int RequestBodyLimit { get; }
    public int ResponseBodyLimit { get; }
    public string[] LoggableBodyContentTypes { get; }
    public Func<IHttpContext, IPAddress?>? ClientAddressResolver { get; }
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Validates and freezes the supplied options.
    /// </summary>
    /// <exception cref="ArgumentException">A textual option is null or empty, or <see cref="HttpLoggingOptions.Level"/> is <see cref="LogLevel.None"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A body limit is negative.</exception>
    /// <exception cref="ArgumentNullException">The time provider is <see langword="null"/>.</exception>
    public static HttpLoggingSnapshot Create(HttpLoggingOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(options.Category);
        ArgumentException.ThrowIfNullOrEmpty(options.RedactedValue);
        ArgumentOutOfRangeException.ThrowIfNegative(options.RequestBodyLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ResponseBodyLimit);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);

        if (options.Level == LogLevel.None)
        {
            throw new ArgumentException(
                "The HTTP logging level must not be LogLevel.None - it could never emit. To silence " +
                "logging for specific endpoints attach HttpLoggingMetadata with HttpLoggingFields.None; " +
                "to silence it globally do not call UseHttpLogging.",
                nameof(options));
        }

        var contentTypes = new List<string>(options.LoggableBodyContentTypes.Count);
        foreach (string contentType in options.LoggableBodyContentTypes)
        {
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                contentTypes.Add(contentType);
            }
        }

        return new HttpLoggingSnapshot(
            options.Fields,
            options.Category,
            options.Level,
            options.LogRequestStart,
            options.AllowedRequestHeaders.ToFrozenSet(),
            options.AllowedResponseHeaders.ToFrozenSet(),
            options.RedactedValue,
            options.RequestBodyLimit,
            options.ResponseBodyLimit,
            contentTypes.ToArray(),
            options.ClientAddressResolver,
            options.TimeProvider);
    }

    /// <summary>
    /// Decides whether a message body with the supplied <c>Content-Type</c> value is eligible
    /// for capture: prefix entries match the start of the media type, <c>+suffix</c> entries
    /// match its structured-syntax suffix. Parameters after <c>;</c> are ignored.
    /// </summary>
    public bool IsBodyContentTypeLoggable(ReadOnlySpan<char> contentTypeValue)
    {
        int parameterSeparator = contentTypeValue.IndexOf(';');
        if (parameterSeparator >= 0)
        {
            contentTypeValue = contentTypeValue[..parameterSeparator];
        }

        contentTypeValue = contentTypeValue.Trim();
        if (contentTypeValue.IsEmpty)
        {
            return false;
        }

        foreach (string entry in LoggableBodyContentTypes)
        {
            bool matches = entry.StartsWith('+')
                ? contentTypeValue.EndsWith(entry, StringComparison.OrdinalIgnoreCase)
                : contentTypeValue.StartsWith(entry, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                return true;
            }
        }

        return false;
    }
}

using System;
using System.Globalization;
using System.Text;

namespace Assimalign.Cohesion.Web.Diagnostics.Internal;

using Assimalign.Cohesion.Logging;

/// <summary>
/// Renders one completed-exchange <see cref="ILoggerEntry"/> (identified by the
/// <see cref="HttpLoggingAttributes"/> contract) as a W3C extended or NCSA common/combined
/// access-log line. All numeric and date rendering goes through invariant
/// <see cref="ISpanFormattable.TryFormat"/> into stack buffers — no reflection, no
/// culture-sensitive fallbacks. Text fields are sanitized against log injection: control
/// characters never reach the file.
/// </summary>
internal static class W3CAccessLogFormatter
{
    /// <summary>The fixed field list every W3C-extended file declares.</summary>
    public const string ExtendedFields =
        "date time c-ip cs-method cs-uri-stem cs-uri-query sc-status cs-bytes sc-bytes time-taken cs-version cs-host cs(User-Agent) cs(Referer)";

    private const string RequestUserAgent = "http.request.header.user-agent";
    private const string RequestReferer = "http.request.header.referer";

    /// <summary>
    /// Appends the directive block that opens a W3C-extended file.
    /// </summary>
    public static void AppendExtendedDirectives(StringBuilder builder, DateTimeOffset timestamp)
    {
        builder.Append("#Version: 1.0\n");
        builder.Append("#Software: Assimalign.Cohesion.Web.Diagnostics\n");
        builder.Append("#Date: ");
        AppendUtc(builder, timestamp, "yyyy-MM-dd HH:mm:ss");
        builder.Append('\n');
        builder.Append("#Fields: ");
        builder.Append(ExtendedFields);
        builder.Append('\n');
    }

    /// <summary>
    /// Appends one access-log line (terminated with <c>\n</c>) for the supplied entry.
    /// </summary>
    public static void AppendLine(StringBuilder builder, ILoggerEntry entry, AccessLogFormat format)
    {
        if (format == AccessLogFormat.W3CExtended)
        {
            AppendExtendedLine(builder, entry);
        }
        else
        {
            AppendNcsaLine(builder, entry, includeCombinedFields: format == AccessLogFormat.Combined);
        }
    }

    private static void AppendExtendedLine(StringBuilder builder, ILoggerEntry entry)
    {
        // date time
        AppendUtc(builder, entry.Timestamp, "yyyy-MM-dd");
        builder.Append(' ');
        AppendUtc(builder, entry.Timestamp, "HH:mm:ss");
        builder.Append(' ');

        AppendExtendedText(builder, entry, HttpLoggingAttributes.ClientAddress);
        builder.Append(' ');
        AppendExtendedText(builder, entry, HttpLoggingAttributes.RequestMethod);
        builder.Append(' ');
        AppendExtendedText(builder, entry, HttpLoggingAttributes.RequestPath);
        builder.Append(' ');
        AppendExtendedText(builder, entry, HttpLoggingAttributes.RequestQuery);
        builder.Append(' ');
        AppendNumber(builder, entry, HttpLoggingAttributes.ResponseStatusCode);
        builder.Append(' ');
        AppendNumber(builder, entry, HttpLoggingAttributes.RequestBodyBytes);
        builder.Append(' ');
        AppendNumber(builder, entry, HttpLoggingAttributes.ResponseBodyBytes);
        builder.Append(' ');

        // time-taken: seconds, millisecond precision.
        if (TryGetDouble(entry, HttpLoggingAttributes.Duration, out double milliseconds))
        {
            AppendInvariant(builder, milliseconds / 1000d, "F3");
        }
        else
        {
            builder.Append('-');
        }

        builder.Append(' ');
        AppendExtendedText(builder, entry, HttpLoggingAttributes.RequestProtocol);
        builder.Append(' ');
        AppendExtendedText(builder, entry, HttpLoggingAttributes.RequestHost);
        builder.Append(' ');
        AppendExtendedText(builder, entry, RequestUserAgent);
        builder.Append(' ');
        AppendExtendedText(builder, entry, RequestReferer);
        builder.Append('\n');
    }

    private static void AppendNcsaLine(StringBuilder builder, ILoggerEntry entry, bool includeCombinedFields)
    {
        // host ident authuser: no identity surface is logged, so ident/authuser are always '-'.
        AppendNcsaBareText(builder, entry, HttpLoggingAttributes.ClientAddress);
        builder.Append(" - - [");
        AppendUtc(builder, entry.Timestamp, "dd/MMM/yyyy:HH:mm:ss");
        builder.Append(" +0000] \"");

        AppendNcsaQuotedPart(builder, GetText(entry, HttpLoggingAttributes.RequestMethod) ?? "-");
        builder.Append(' ');
        AppendNcsaQuotedPart(builder, GetText(entry, HttpLoggingAttributes.RequestPath) ?? "-");

        if (GetText(entry, HttpLoggingAttributes.RequestQuery) is { Length: > 0 } query)
        {
            builder.Append('?');
            AppendNcsaQuotedPart(builder, query);
        }

        builder.Append(' ');
        AppendNcsaQuotedPart(builder, GetText(entry, HttpLoggingAttributes.RequestProtocol) ?? "-");
        builder.Append("\" ");

        AppendNumber(builder, entry, HttpLoggingAttributes.ResponseStatusCode);
        builder.Append(' ');
        AppendNumber(builder, entry, HttpLoggingAttributes.ResponseBodyBytes);

        if (includeCombinedFields)
        {
            builder.Append(" \"");
            AppendNcsaQuotedPart(builder, GetText(entry, RequestReferer) ?? "-");
            builder.Append("\" \"");
            AppendNcsaQuotedPart(builder, GetText(entry, RequestUserAgent) ?? "-");
            builder.Append('"');
        }

        builder.Append('\n');
    }

    private static string? GetText(ILoggerEntry entry, string attribute)
        => entry.Attributes.TryGetValue(attribute, out object? value) && value is string text && text.Length > 0
            ? text
            : null;

    private static bool TryGetDouble(ILoggerEntry entry, string attribute, out double result)
    {
        if (entry.Attributes.TryGetValue(attribute, out object? value) && value is double d)
        {
            result = d;
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// W3C-extended text field: spaces become <c>+</c> (the IIS convention, so the
    /// space-separated line stays parseable) and control characters are dropped.
    /// </summary>
    private static void AppendExtendedText(StringBuilder builder, ILoggerEntry entry, string attribute)
    {
        string? text = GetText(entry, attribute);
        if (text is null)
        {
            builder.Append('-');
            return;
        }

        foreach (char c in text)
        {
            if (c is < ' ' or '\x7f')
            {
                continue;
            }

            builder.Append(c == ' ' ? '+' : c);
        }
    }

    /// <summary>
    /// NCSA unquoted field (the host position): whitespace and control characters are dropped
    /// so the field cannot split or forge the line.
    /// </summary>
    private static void AppendNcsaBareText(StringBuilder builder, ILoggerEntry entry, string attribute)
    {
        string? text = GetText(entry, attribute);
        if (text is null)
        {
            builder.Append('-');
            return;
        }

        foreach (char c in text)
        {
            if (c is <= ' ' or '\x7f')
            {
                continue;
            }

            builder.Append(c);
        }
    }

    /// <summary>
    /// NCSA quoted-field content: quotes and backslashes are escaped, control characters
    /// dropped.
    /// </summary>
    private static void AppendNcsaQuotedPart(StringBuilder builder, string text)
    {
        foreach (char c in text)
        {
            if (c is < ' ' or '\x7f')
            {
                continue;
            }

            if (c is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }
    }

    private static void AppendNumber(StringBuilder builder, ILoggerEntry entry, string attribute)
    {
        if (!entry.Attributes.TryGetValue(attribute, out object? value))
        {
            builder.Append('-');
            return;
        }

        switch (value)
        {
            case int number:
                AppendInvariant(builder, number);
                break;
            case long number:
                AppendInvariant(builder, number);
                break;
            default:
                builder.Append('-');
                break;
        }
    }

    private static void AppendInvariant(StringBuilder builder, long value)
    {
        Span<char> buffer = stackalloc char[20];
        value.TryFormat(buffer, out int written, provider: CultureInfo.InvariantCulture);
        builder.Append(buffer[..written]);
    }

    private static void AppendInvariant(StringBuilder builder, double value, string format)
    {
        Span<char> buffer = stackalloc char[32];
        if (value.TryFormat(buffer, out int written, format, CultureInfo.InvariantCulture))
        {
            builder.Append(buffer[..written]);
        }
        else
        {
            builder.Append(value.ToString(format, CultureInfo.InvariantCulture));
        }
    }

    private static void AppendUtc(StringBuilder builder, DateTimeOffset timestamp, string format)
    {
        Span<char> buffer = stackalloc char[32];
        if (timestamp.UtcDateTime.TryFormat(buffer, out int written, format, CultureInfo.InvariantCulture))
        {
            builder.Append(buffer[..written]);
        }
        else
        {
            builder.Append(timestamp.UtcDateTime.ToString(format, CultureInfo.InvariantCulture));
        }
    }
}

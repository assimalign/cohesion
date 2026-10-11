using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Health.Internal;

using Assimalign.Cohesion.Http;

/// <summary>
/// The default <see cref="IHealthResponseWriter"/>. Emits the health report as
/// <c>application/health+json</c> using a hand-written <see cref="Utf8JsonWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Serialization is fully hand-written — no reflection-based <c>JsonSerializer</c> — so the
/// writer is trim- and NativeAOT-safe (mirrors the <c>OpenApiJsonWriter</c> precedent). Diagnostic
/// <see cref="HealthReportEntry.Data"/> values are written through a closed type switch; anything
/// outside the known primitive set, other than an <see cref="Exception"/>, falls back to its
/// <see cref="object.ToString"/> form rather than pulling in a runtime serializer.
/// </para>
/// <para>
/// <see cref="Instance"/> publishes no exception text (CWE-209): it never writes an entry's
/// <see cref="HealthReportEntry.Exception"/>, it replaces a description that contains the
/// message of that exception or of any exception in its <see cref="Exception.InnerException"/>
/// chain with <see cref="ReportedExceptionDescription"/>, and it writes the same text for an
/// <see cref="Exception"/> placed in <see cref="HealthReportEntry.Data"/>.
/// <see cref="DeveloperDetails"/>, which <see cref="HealthEndpointOptions.IncludeDeveloperDetails"/>
/// selects, writes each description as the entry carries it, the top-level exception's
/// <see cref="Exception.Message"/> as an <c>exception</c> member, and an exception in
/// <see cref="HealthReportEntry.Data"/> as its message only. The service's fixed descriptions for a
/// thrown or timed-out check apply to both instances, so neither reproduces the 10.0.0-preview.1
/// output exactly (see <see cref="HealthEndpointOptions.IncludeDeveloperDetails"/>).
/// </para>
/// </remarks>
internal sealed class HealthCheckJsonResponseWriter : IHealthResponseWriter
{
    /// <summary>
    /// The shared default instance, which publishes no exception text.
    /// </summary>
    public static readonly HealthCheckJsonResponseWriter Instance = new(includeDeveloperDetails: false);

    /// <summary>
    /// The shared instance that also publishes each entry's exception message, selected by
    /// <see cref="HealthEndpointOptions.IncludeDeveloperDetails"/>.
    /// </summary>
    public static readonly HealthCheckJsonResponseWriter DeveloperDetails = new(includeDeveloperDetails: true);

    /// <summary>
    /// The description <see cref="Instance"/> writes in place of one that contains an exception
    /// message.
    /// </summary>
    internal const string ReportedExceptionDescription = "The check reported an exception.";

    private const string HealthJsonContentType = "application/health+json; charset=utf-8";

    private readonly bool _includeDeveloperDetails;

    private HealthCheckJsonResponseWriter(bool includeDeveloperDetails)
    {
        _includeDeveloperDetails = includeDeveloperDetails;
    }

    public async ValueTask WriteAsync(IHttpContext context, HealthReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.Headers[HttpHeaderKey.ContentType] = HealthJsonContentType;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteReport(writer, report);
        }

        await context.Response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    private void WriteReport(Utf8JsonWriter writer, HealthReport report)
    {
        writer.WriteStartObject();
        writer.WriteString("status", ToStatusString(report.Status));
        writer.WriteNumber("totalDurationMs", report.TotalDuration.TotalMilliseconds);

        writer.WriteStartObject("entries");
        foreach (KeyValuePair<string, HealthReportEntry> entry in report.Entries)
        {
            writer.WritePropertyName(entry.Key);
            WriteEntry(writer, entry.Value);
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private void WriteEntry(Utf8JsonWriter writer, HealthReportEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("status", ToStatusString(entry.Status));
        writer.WriteNumber("durationMs", entry.Duration.TotalMilliseconds);

        string? description = _includeDeveloperDetails ? entry.Description : PublicDescription(entry);
        if (description is not null)
        {
            writer.WriteString("description", description);
        }

        if (_includeDeveloperDetails && entry.Exception is not null)
        {
            writer.WriteString("exception", entry.Exception.Message);
        }

        writer.WriteStartArray("tags");
        foreach (string tag in entry.Tags)
        {
            writer.WriteStringValue(tag);
        }
        writer.WriteEndArray();

        if (entry.Data.Count > 0)
        {
            writer.WriteStartObject("data");
            foreach (KeyValuePair<string, object> item in entry.Data)
            {
                writer.WritePropertyName(item.Key);
                WriteDataValue(writer, item.Value);
            }
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Returns the description the default writer may publish: the entry's own description, unless
    /// it contains the message of the entry's exception or of an exception in its inner chain. A
    /// check that returns <c>HealthCheckResult.Unhealthy(exception.Message, exception)</c>, or
    /// formats the message into its description, would otherwise publish the exception text.
    /// </summary>
    private static string? PublicDescription(HealthReportEntry entry)
    {
        string? description = entry.Description;
        if (description is null or HealthCheckService.ThrewDescription or HealthCheckService.TimedOutDescription ||
            entry.Exception is null)
        {
            return description;
        }

        for (Exception? exception = entry.Exception; exception is not null; exception = exception.InnerException)
        {
            string message = exception.Message;
            if (message.Length > 0 && description.Contains(message, StringComparison.Ordinal))
            {
                return ReportedExceptionDescription;
            }
        }

        return description;
    }

    private void WriteDataValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case Exception exception:
                // ToString() would write the type, message, inner exceptions and stack trace. The
                // default instance withholds all of it; the developer instance writes the message
                // only, as it does for the entry's own exception.
                writer.WriteStringValue(_includeDeveloperDetails ? exception.Message : ReportedExceptionDescription);
                break;
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            default:
                // Closed serialization: no reflection fallback. Anything exotic is stringified.
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static string ToStatusString(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "Healthy",
        HealthStatus.Degraded => "Degraded",
        HealthStatus.Unhealthy => "Unhealthy",
        _ => "Unhealthy",
    };
}

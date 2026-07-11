using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Assimalign.Cohesion.Web.Internal;

/// <summary>
/// The <c>application/problem+json</c> implementation of <see cref="IProblemDetailsWriter"/>. Renders
/// a <see cref="ProblemDetails"/> directly through <see cref="Utf8JsonWriter"/> &#8212; walking the
/// five standard members and the extension bag explicitly &#8212; so serialization is AOT- and
/// trimming-safe with no reflection, following the <c>OpenApiJsonWriter</c> precedent.
/// </summary>
/// <remarks>
/// The default (safe) <see cref="Utf8JsonWriter"/> encoder is used deliberately: it HTML-escapes
/// characters that would be dangerous if a problem+json body were ever mis-rendered as HTML, which
/// matters because <see cref="ProblemDetails.Detail"/> can echo request-influenced text.
/// </remarks>
internal sealed class ProblemDetailsJsonWriter : IProblemDetailsWriter
{
    /// <summary>The shared, stateless default writer instance.</summary>
    public static ProblemDetailsJsonWriter Instance { get; } = new();

    /// <inheritdoc />
    public void Write(ProblemDetails problem, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(stream);

        using var writer = new Utf8JsonWriter(stream);
        WriteCore(writer, problem);
        writer.Flush();
    }

    /// <inheritdoc />
    public byte[] WriteToUtf8Bytes(ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCore(writer, problem);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <inheritdoc />
    public string WriteToString(ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCore(writer, problem);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCore(Utf8JsonWriter writer, ProblemDetails problem)
    {
        writer.WriteStartObject();

        // Standard members (RFC 9457 §3.1) — "type" always emitted (defaults to "about:blank").
        writer.WriteString("type", problem.Type ?? ProblemDetailsDefaults.DefaultType);

        if (problem.Title is not null)
        {
            writer.WriteString("title", problem.Title);
        }

        if (problem.Status is int status)
        {
            writer.WriteNumber("status", status);
        }

        if (problem.Detail is not null)
        {
            writer.WriteString("detail", problem.Detail);
        }

        if (problem.Instance is not null)
        {
            writer.WriteString("instance", problem.Instance);
        }

        // Extension members (RFC 9457 §3.2). Skip any key that collides with a standard member so a
        // stray extension cannot emit a duplicate JSON property.
        foreach (KeyValuePair<string, object?> extension in problem.Extensions)
        {
            if (IsReservedMember(extension.Key))
            {
                continue;
            }

            writer.WritePropertyName(extension.Key);
            WriteValue(writer, extension.Value);
        }

        writer.WriteEndObject();
    }

    private static bool IsReservedMember(string key)
    {
        return key is "type" or "title" or "status" or "detail" or "instance";
    }

    /// <summary>
    /// Writes a single extension value. The type switch is an explicit, closed allow-list — no
    /// reflection — so it stays AOT-safe. Unknown types degrade to their string form rather than
    /// throwing, keeping the writer safe to call from the last-chance exception boundary.
    /// </summary>
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case short sh:
                writer.WriteNumberValue(sh);
                break;
            case byte b:
                writer.WriteNumberValue(b);
                break;
            case sbyte sb:
                writer.WriteNumberValue(sb);
                break;
            case ushort us:
                writer.WriteNumberValue(us);
                break;
            case uint ui:
                writer.WriteNumberValue(ui);
                break;
            case ulong ul:
                writer.WriteNumberValue(ul);
                break;
            // A string-keyed map serializes as a nested object. Checked before the generic sequence
            // branch because a dictionary is also IEnumerable.
            case IEnumerable<KeyValuePair<string, object?>> members:
                WriteObject(writer, members);
                break;
            // Any other sequence serializes as an array (string was matched above, so this never
            // sees a string's chars).
            case IEnumerable sequence:
                WriteArray(writer, sequence);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> members)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<string, object?> member in members)
        {
            writer.WritePropertyName(member.Key);
            WriteValue(writer, member.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteArray(Utf8JsonWriter writer, IEnumerable items)
    {
        writer.WriteStartArray();
        foreach (object? item in items)
        {
            WriteValue(writer, item);
        }

        writer.WriteEndArray();
    }
}

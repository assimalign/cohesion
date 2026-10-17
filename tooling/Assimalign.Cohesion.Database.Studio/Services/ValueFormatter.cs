using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph;

namespace Assimalign.Cohesion.Database.Studio;

/// <summary>Turns engine/client values into display text.</summary>
internal static class ValueFormatter
{
    public const string NullText = "NULL";

    public static string Format(object? value)
    {
        switch (value)
        {
            case null:
                return NullText;
            case string text:
                return text;
            case bool flag:
                return flag ? "true" : "false";
            case byte[] bytes:
                return Bytes.ToDisplay(bytes);
            case ReadOnlyMemory<byte> memory:
                return Bytes.ToDisplay(memory.Span);
            case Memory<byte> memory:
                return Bytes.ToDisplay(memory.Span);
            case JsonElement element:
                return element.ValueKind == JsonValueKind.String ? element.GetString() ?? NullText : element.GetRawText();
            case DateTime dateTime:
                return dateTime.ToString("O", CultureInfo.InvariantCulture);
            case DateTimeOffset dateTimeOffset:
                return dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
            case GraphNode node:
                return FormatNode(node);
            case GraphRelationship relationship:
                return FormatRelationship(relationship, null);
            case GraphPath path:
                return FormatPath(path);
            case IReadOnlyDictionary<string, object?> map:
                return FormatMap(map);
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            case IEnumerable sequence:
            {
                var builder = new StringBuilder("[");
                bool first = true;
                foreach (object? item in sequence)
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(Format(item));
                    first = false;
                }

                return builder.Append(']').ToString();
            }
            default:
                return value.ToString() ?? string.Empty;
        }
    }

    public static string FormatMap(IReadOnlyDictionary<string, object?> map)
    {
        if (map.Count == 0)
        {
            return "{}";
        }

        var builder = new StringBuilder("{");
        bool first = true;
        foreach (var (key, value) in map.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(", ");
            }

            builder.Append(key).Append(": ").Append(value is string text ? $"'{text}'" : Format(value));
            first = false;
        }

        return builder.Append('}').ToString();
    }

    public static string FormatNode(GraphNode node)
    {
        var builder = new StringBuilder("(#").Append(node.Id.Value);
        foreach (string label in node.Labels)
        {
            builder.Append(':').Append(label);
        }

        if (node.Properties.Count > 0)
        {
            builder.Append(' ').Append(FormatMap(node.Properties));
        }

        return builder.Append(')').ToString();
    }

    /// <summary>Formats a relationship; when <paramref name="forward"/> is known the arrow follows the path direction.</summary>
    public static string FormatRelationship(GraphRelationship relationship, bool? forward)
    {
        string body = $"[#{relationship.Id.Value}:{relationship.Type}{(relationship.Properties.Count > 0 ? " " + FormatMap(relationship.Properties) : string.Empty)}]";
        return forward switch
        {
            true => $"-{body}->",
            false => $"<-{body}-",
            null => $"(#{relationship.From.Value})-{body}->(#{relationship.To.Value})",
        };
    }

    /// <summary>Renders <c>(a:Label {..})-[:TYPE]-&gt;(b ...)</c> in traversal order.</summary>
    public static string FormatPath(GraphPath path)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < path.Nodes.Count; i++)
        {
            builder.Append(FormatNode(path.Nodes[i]));
            if (i < path.Relationships.Count)
            {
                GraphRelationship relationship = path.Relationships[i];
                bool forward = relationship.From == path.Nodes[i].Id;
                builder.Append(FormatRelationship(relationship, forward));
            }
        }

        return builder.ToString();
    }
}

/// <summary>Byte helpers: UTF-8 text with hex fallback.</summary>
internal static class Bytes
{
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryUtf8(ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = _strictUtf8.GetString(bytes);
            foreach (char c in text)
            {
                if (char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
                {
                    return false;
                }
            }

            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    public static string ToHex(ReadOnlySpan<byte> bytes) => "0x" + Convert.ToHexString(bytes);

    public static string ToDisplay(ReadOnlySpan<byte> bytes)
        => bytes.IsEmpty ? "(empty)" : TryUtf8(bytes, out string text) ? text : ToHex(bytes);

    /// <summary>Parses user input as UTF-8 text or hex (<c>0x</c> prefix optional, whitespace ignored).</summary>
    public static byte[] Parse(string? input, bool hex)
    {
        input ??= string.Empty;
        if (!hex)
        {
            return Encoding.UTF8.GetBytes(input);
        }

        string compact = new([.. input.Where(c => !char.IsWhiteSpace(c))]);
        if (compact.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            compact = compact[2..];
        }

        if (compact.Length % 2 != 0)
        {
            throw new FormatException("Hex input must contain an even number of digits.");
        }

        return Convert.FromHexString(compact);
    }
}

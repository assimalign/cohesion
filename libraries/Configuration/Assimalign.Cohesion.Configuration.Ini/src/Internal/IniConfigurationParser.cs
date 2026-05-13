using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Ini;

using Assimalign.Cohesion.Configuration;

/// <summary>
/// Parses the Cohesion-supported INI grammar from a <see cref="Stream"/> into a flat
/// <see cref="Path"/>-keyed entry table.
/// </summary>
/// <remarks>
/// The supported grammar is intentionally small. See <c>docs/DESIGN.md</c> for the
/// authoritative description; the short version:
/// <list type="bullet">
///   <item><description>Section headers: <c>[name]</c>. A section is in effect until the next
///     section header or end-of-file. Section names use the configured separator
///     (default <c>:</c>) to denote nesting, so <c>[Logging:Console]</c> nests <c>Console</c>
///     under <c>Logging</c>.</description></item>
///   <item><description>Sectionless root keys: <c>key = value</c> outside any section maps directly
///     to <c>key</c> at the root.</description></item>
///   <item><description>Key/value: <c>key = value</c>. The first <c>=</c> is the delimiter;
///     subsequent <c>=</c> characters are preserved literally in the value.</description></item>
///   <item><description>Whitespace around the section name, key, and either side of the
///     delimiter is trimmed. Whitespace inside a value is preserved as-is.</description></item>
///   <item><description>Comments: lines whose first non-whitespace character is <c>;</c> or
///     <c>#</c> are ignored. Comments are not recognized mid-line.</description></item>
///   <item><description>Blank lines are ignored.</description></item>
///   <item><description>Quoted values (<c>"..."</c> or <c>'...'</c>) are treated as literal text;
///     surrounding quotes are stripped but the inner text is not unescaped.</description></item>
///   <item><description>Duplicate keys within the provider resolve last-value-wins.</description></item>
///   <item><description>Multi-line continuations and escape sequences are explicit non-goals.</description></item>
/// </list>
/// </remarks>
internal static class IniConfigurationParser
{
    /// <summary>
    /// Reads INI content from <paramref name="stream"/> and populates <paramref name="entries"/>.
    /// </summary>
    /// <param name="stream">A readable, seekable-or-not stream containing INI content.</param>
    /// <param name="entries">The entry dictionary populated by the parser.</param>
    /// <param name="cancellationToken">A token to observe while the parser runs.</param>
    /// <returns>A task that completes when the entire stream has been consumed.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="stream"/> or <paramref name="entries"/> is null.
    /// </exception>
    /// <exception cref="FormatException">
    /// Thrown when a section header or key/value assignment is malformed.
    /// </exception>
    public static async Task ParseAsync(
        Stream stream,
        IDictionary<Path, string?> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);

        // leaveOpen: the provider that owns the stream is responsible for disposal.
        // detectEncodingFromByteOrderMarks: BOM-aware to keep UTF-8-BOM files working.
        using var reader = new StreamReader(
            stream,
            encoding: Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);

        var section = new List<Key>(capacity: 4);
        int lineNumber = 0;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } rawLine)
        {
            lineNumber++;
            cancellationToken.ThrowIfCancellationRequested();

            ReadOnlySpan<char> line = rawLine.AsSpan().Trim();

            // Blank line or comment - skip.
            if (line.IsEmpty || IsComment(line))
            {
                continue;
            }

            // Section header.
            if (line[0] == '[')
            {
                section = ParseSectionHeader(line, lineNumber);
                continue;
            }

            // Key/value.
            ParseAssignment(line, section, entries, lineNumber);
        }
    }

    private static bool IsComment(ReadOnlySpan<char> trimmed)
        => trimmed[0] == ';' || trimmed[0] == '#';

    private static List<Key> ParseSectionHeader(ReadOnlySpan<char> line, int lineNumber)
    {
        if (line[^1] != ']')
        {
            throw new FormatException(
                $"INI line {lineNumber}: section header must end with ']'. Got: '{line.ToString()}'.");
        }

        // Strip the brackets and trim whitespace inside them.
        ReadOnlySpan<char> body = line[1..^1].Trim();

        if (body.IsEmpty)
        {
            throw new FormatException(
                $"INI line {lineNumber}: section header is empty ('[]' is not a valid section name).");
        }

        // Each ':'-separated piece becomes a Key. Each piece is itself trimmed so
        // '[ A : B ]' resolves to ['A', 'B'].
        var keys = new List<Key>(capacity: 2);
        int start = 0;

        for (int index = 0; index <= body.Length; index++)
        {
            bool atDelimiter = index == body.Length || body[index] == ':';
            if (!atDelimiter)
            {
                continue;
            }

            ReadOnlySpan<char> segment = body[start..index].Trim();
            if (segment.IsEmpty)
            {
                throw new FormatException(
                    $"INI line {lineNumber}: section header '[{body.ToString()}]' contains an empty segment.");
            }

            keys.Add(new Key(segment.ToString()));
            start = index + 1;
        }

        return keys;
    }

    private static void ParseAssignment(
        ReadOnlySpan<char> line,
        List<Key> section,
        IDictionary<Path, string?> entries,
        int lineNumber)
    {
        int delimiter = line.IndexOf('=');
        if (delimiter < 0)
        {
            throw new FormatException(
                $"INI line {lineNumber}: expected 'key = value' assignment but no '=' was found. Got: '{line.ToString()}'.");
        }

        ReadOnlySpan<char> keyPart = line[..delimiter].Trim();
        if (keyPart.IsEmpty)
        {
            throw new FormatException(
                $"INI line {lineNumber}: assignment is missing a key. Got: '{line.ToString()}'.");
        }

        // Value side: trim whitespace, then strip a single surrounding matched pair
        // of quotes if present. Internal content stays literal.
        ReadOnlySpan<char> valuePart = line[(delimiter + 1)..].Trim();
        string value = UnquoteIfWrapped(valuePart);

        // The fully-qualified path is the current section keys plus the assignment key.
        // Last-value-wins on duplicate keys.
        var pathKeys = new Key[section.Count + 1];
        for (int index = 0; index < section.Count; index++)
        {
            pathKeys[index] = section[index];
        }
        pathKeys[^1] = new Key(keyPart.ToString());

        var path = new Path(pathKeys);
        entries[path] = value;
    }

    private static string UnquoteIfWrapped(ReadOnlySpan<char> value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].ToString();
        }

        return value.ToString();
    }
}

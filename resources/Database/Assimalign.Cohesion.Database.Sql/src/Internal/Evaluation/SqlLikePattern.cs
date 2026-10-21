using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A tokenized LIKE pattern. Escapes and wildcards are read before literal normalization.</summary>
internal sealed class SqlLikePattern
{
    // A null literal is %, an empty literal is _, and a nonempty literal is a literal run.
    private readonly string?[] _tokens;
    private readonly Collation? _normalizedFor;
    private readonly string?[]? _normalizedTokens;

    private SqlLikePattern(string?[] tokens, Collation? normalizedFor = null, string?[]? normalizedTokens = null)
    {
        _tokens = tokens;
        _normalizedFor = normalizedFor;
        _normalizedTokens = normalizedTokens;
    }

    internal static void ValidateEscape(string escape)
    {
        if (escape.Length != 0 && (!Rune.TryGetRuneAt(escape, 0, out var rune) || rune.Utf16SequenceLength != escape.Length))
        {
            throw new SqlPlanningException("COHSQLE034", ErrorMessages.LikeEscapeInvalid);
        }
    }

    // PostgreSQL like_match.c:428-439,444-453,473-476: empty disables escaping,
    // one character escapes any following character, and escape wins over % and _.
    internal static SqlLikePattern Compile(string pattern, string escape, CancellationToken cancellationToken)
    {
        ValidateEscape(escape);
        var tokens = new List<string?>();
        var literal = new StringBuilder();
        int steps = 0;
        for (int index = 0; index < pattern.Length;)
        {
            if ((steps++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            if (escape.Length != 0 && pattern.AsSpan(index).StartsWith(escape, StringComparison.Ordinal))
            {
                index += escape.Length;
                if (index == pattern.Length) { throw new SqlPlanningException("COHSQLE034", ErrorMessages.LikeEscapeInvalid); }
                int length = ScalarLength(pattern, index);
                literal.Append(pattern, index, length);
                index += length;
            }
            else if (pattern[index] is '%' or '_')
            {
                if (literal.Length > 0) { tokens.Add(literal.ToString()); literal.Clear(); }
                string? token = pattern[index++] == '%' ? null : string.Empty;
                if (token is not null || tokens.Count == 0 || tokens[^1] is not null) { tokens.Add(token); }
            }
            else
            {
                int length = ScalarLength(pattern, index);
                literal.Append(pattern, index, length);
                index += length;
            }
        }
        if (literal.Length > 0) { tokens.Add(literal.ToString()); }
        return new SqlLikePattern(tokens.ToArray());
    }

    internal SqlLikePattern Normalize(Collation collation)
    {
        if (!collation.IsIndexBacked || ReferenceEquals(_normalizedFor, collation)) { return this; }
        var tokens = new string?[_tokens.Length];
        for (int index = 0; index < tokens.Length; index++)
        {
            tokens[index] = _tokens[index] is { } literal ? collation.Normalize(literal) : null;
        }
        // Keep raw tokens: a literal reduced to empty is still distinct from '_'.
        return new SqlLikePattern(_tokens, collation, tokens);
    }

    internal bool Match(string input, Collation collation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pattern = Normalize(collation);
        if (ReferenceEquals(collation, Collation.Binary))
        {
            collation.Normalize(input); // Retain validation of Unicode scalars on the binary fast path.
            return MatchOrdinal(input, _tokens, cancellationToken);
        }
        if (ReferenceEquals(collation, Collation.CaseInsensitive))
        {
            // Simple case folding preserves one scalar per input scalar, so wildcard
            // boundaries survive folding and only the last '%' needs to be remembered.
            return MatchOrdinal(collation.Normalize(input), pattern._normalizedTokens!, cancellationToken);
        }
        string normalized = input;
        int[]? offsets = null;
        if (collation.IsIndexBacked && !ReferenceEquals(collation, Collation.Binary))
        {
            normalized = collation.Normalize(input);
            offsets = new int[input.Length + 1];
            var lengths = new Dictionary<int, int>();
            int offset = 0, mapped = 0;
            for (int index = 0; index < input.Length;)
            {
                if ((mapped++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                int scalar = char.ConvertToUtf32(input, index);
                int length = ScalarLength(input, index);
                if (!lengths.TryGetValue(scalar, out int foldedLength))
                {
                    foldedLength = collation.Normalize(new Rune(scalar).ToString()).Length;
                    lengths.Add(scalar, foldedLength);
                }
                index += length;
                offset += foldedLength;
                offsets[index] = offset;
            }
        }
        else if (!collation.IsIndexBacked)
        {
            // Preserve Collation's explicit refusal under invariant globalization.
            collation.Compare(string.Empty, string.Empty);
        }

        // PostgreSQL like_match.c:193-221 advances wildcards over original characters,
        // comparing literal runs separately. Frames and failed suffixes bound repeated work.
        var pending = new Stack<(int Token, int Start, int End)>();
        // Allocate rows lazily: at most one bit per token/input boundary, rather
        // than one hash entry per state on repetitive nonmatching input.
        var visited = new uint[_tokens.Length][];
        pending.Push((0, 0, -1));
        int steps = 0;
        while (pending.TryPop(out var state))
        {
            if ((steps++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            int index = state.Token, start = state.Start;
            if (index == _tokens.Length)
            {
                if (start == input.Length) { return true; }
                continue;
            }
            if (state.End < 0)
            {
                var row = visited[index] ??= new uint[(int)(((long)input.Length + 32) / 32)];
                uint mask = 1u << (start & 31);
                if ((row[start >> 5] & mask) != 0) { continue; }
                row[start >> 5] |= mask;
            }
            string? raw = _tokens[index];
            if (raw is null)
            {
                if (index + 1 == _tokens.Length) { return true; }
                if (start < input.Length) { pending.Push((index, start + ScalarLength(input, start), -1)); }
                pending.Push((index + 1, start, -1));
            }
            else if (raw.Length == 0)
            {
                if (start < input.Length) { pending.Push((index + 1, start + ScalarLength(input, start), -1)); }
            }
            else
            {
                string token = pattern._normalizedTokens?[index] ?? raw;
                int normalizedStart = offsets?[start] ?? start;
                // The deterministic transform only appends. Compare the complete run
                // once, then find original boundaries at its normalized end offset.
                if (collation.IsIndexBacked && !normalized.AsSpan(normalizedStart).StartsWith(token, StringComparison.Ordinal)) { continue; }
                for (int end = state.End < 0 ? start : state.End; end <= input.Length;)
                {
                    if ((steps++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                    bool equal;
                    if (collation.IsIndexBacked)
                    {
                        int length = offsets![end] - normalizedStart;
                        if (length > token.Length) { break; }
                        equal = length == token.Length;
                    }
                    else
                    {
                        var prefix = input.AsSpan(start, end - start);
                        var compare = CultureInfo.InvariantCulture.CompareInfo;
                        // Linguistic prefixes can reorder (for example Thai/Lao).
                        // Probe every scalar boundary, as like_match.c:289-311 does.
                        equal = compare.Compare(prefix, token.AsSpan(), CompareOptions.None) == 0;
                    }
                    if (equal)
                    {
                        if (end < input.Length)
                        {
                            int next = end + ScalarLength(input, end);
                            // Only ignored scalars can extend an equal normalized literal.
                            if (!collation.IsIndexBacked || offsets![next] == offsets[end]) { pending.Push((index, start, next)); }
                        }
                        pending.Push((index + 1, end, -1));
                        break;
                    }
                    if (end == input.Length) { break; }
                    end += ScalarLength(input, end);
                }
            }
        }
        return false;
    }

    private static bool MatchOrdinal(string input, string?[] tokens, CancellationToken cancellationToken)
    {
        int position = 0, tokenIndex = 0, star = -1, retry = 0, steps = 0;
        // Iterative backtracking to the last %, bounded stack even for long dynamic patterns.
        while (true)
        {
            if ((steps++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            if (tokenIndex == tokens.Length && position == input.Length) { return true; }
            if (tokenIndex < tokens.Length)
            {
                string? token = tokens[tokenIndex];
                if (token is null)
                {
                    if (tokenIndex + 1 == tokens.Length) { return true; }
                    star = tokenIndex++;
                    retry = position;
                    continue;
                }
                if (token.Length == 0 && position < input.Length)
                {
                    position += ScalarLength(input, position);
                    tokenIndex++;
                    continue;
                }
                if (token.Length > 0 && input.AsSpan(position).StartsWith(token, StringComparison.Ordinal))
                {
                    position += token.Length;
                    tokenIndex++;
                    continue;
                }
            }
            if (star < 0 || retry == input.Length) { return false; }
            retry += ScalarLength(input, retry);
            position = retry;
            tokenIndex = star + 1;
        }
    }

    private static int ScalarLength(string text, int position)
        => char.IsHighSurrogate(text[position]) && position + 1 < text.Length && char.IsLowSurrogate(text[position + 1]) ? 2 : 1;
}

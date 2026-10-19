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
            return MatchBinary(input, cancellationToken);
        }
        string normalized = input;
        int[]? offsets = null;
        bool[]? elementEnds = null;
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
            elementEnds = new bool[input.Length + 1];
            int elements = 0;
            for (int end = 0; end < input.Length;)
            {
                if ((elements++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                end += StringInfo.GetNextTextElementLength(input.AsSpan(end));
                elementEnds[end] = true;
            }
        }

        // PostgreSQL like_match.c:193-221 advances wildcards over original characters,
        // comparing literal runs separately. Frames and failed suffixes bound repeated work.
        var pending = new Stack<(int Token, int Start, int End)>();
        var visited = new HashSet<(int Token, int Start)>();
        pending.Push((0, 0, -1));
        int steps = 0;
        while (pending.TryPop(out var state))
        {
            if ((steps++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            int index = state.Token, start = state.Start;
            if (state.End < 0 && !visited.Add((index, start))) { continue; }
            if (index == _tokens.Length)
            {
                if (start == input.Length) { return true; }
                continue;
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
                for (int end = state.End < 0 ? start : state.End; end <= input.Length;)
                {
                    if ((steps++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                    bool equal;
                    if (collation.IsIndexBacked)
                    {
                        var prefix = normalized.AsSpan(normalizedStart, (offsets?[end] ?? end) - normalizedStart);
                        // Normalization is scalar-local and only appends. Once a prefix differs
                        // or exceeds the literal, no longer original prefix can match it.
                        if (!token.AsSpan().StartsWith(prefix, StringComparison.Ordinal)) { break; }
                        equal = prefix.Length == token.Length;
                    }
                    else
                    {
                        var prefix = input.AsSpan(start, end - start);
                        var compare = CultureInfo.InvariantCulture.CompareInfo;
                        // Prune only at complete text elements: a following scalar can finish
                        // an accent or Hangul syllable. Equality still probes every raw scalar
                        // boundary, allowing a wildcard to consume a combining mark itself.
                        if (elementEnds![end] && !compare.IsPrefix(token.AsSpan(), prefix, CompareOptions.IgnoreNonSpace)) { break; }
                        equal = compare.Compare(prefix, token.AsSpan(), CompareOptions.None) == 0;
                    }
                    if (equal)
                    {
                        if (end < input.Length) { pending.Push((index, start, end + ScalarLength(input, end))); }
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

    private bool MatchBinary(string input, CancellationToken cancellationToken)
    {
        int position = 0, tokenIndex = 0, star = -1, retry = 0, steps = 0;
        // Iterative backtracking to the last %, bounded stack even for long dynamic patterns.
        while (true)
        {
            if ((steps++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            if (tokenIndex == _tokens.Length && position == input.Length) { return true; }
            if (tokenIndex < _tokens.Length)
            {
                string? token = _tokens[tokenIndex];
                if (token is null)
                {
                    if (tokenIndex + 1 == _tokens.Length) { return true; }
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

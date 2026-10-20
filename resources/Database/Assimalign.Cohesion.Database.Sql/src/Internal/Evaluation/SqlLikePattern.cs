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
    private readonly int _sourceLength;
    private readonly Collation? _normalizedFor;
    private readonly string?[]? _normalizedTokens;

    private SqlLikePattern(string?[] tokens, int sourceLength, Collation? normalizedFor = null, string?[]? normalizedTokens = null)
    {
        _tokens = tokens;
        _sourceLength = sourceLength;
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
        return new SqlLikePattern(tokens.ToArray(), pattern.Length);
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
        return new SqlLikePattern(_tokens, _sourceLength, collation, tokens);
    }

    internal bool Match(string input, Collation collation, CancellationToken cancellationToken)
        => Match(input, collation, cancellationToken, out _);

    // Work counts search transitions and UTF-16 units offered to literal comparisons.
    // A ref/out counter permits deterministic resource tests without a per-match object.
    internal bool Match(string input, Collation collation, CancellationToken cancellationToken, out long work)
    {
        work = 0;
        cancellationToken.ThrowIfCancellationRequested();
        long limit = WorkLimit(input.Length);
        var pattern = Normalize(collation);
        if (ReferenceEquals(collation, Collation.Binary))
        {
            collation.Normalize(input); // Retain validation of Unicode scalars on the binary fast path.
            return MatchOrdinal(input, _tokens, cancellationToken, ref work);
        }
        if (ReferenceEquals(collation, Collation.CaseInsensitive))
        {
            // Simple case folding preserves one scalar per input scalar, so wildcard
            // boundaries survive folding and only the last '%' needs to be remembered.
            return MatchOrdinal(collation.Normalize(input), pattern._normalizedTokens!, cancellationToken, ref work);
        }
        string normalized = input;
        int[]? offsets = null;
        int[]? firstBoundaries = null;
        if (collation.IsIndexBacked && !ReferenceEquals(collation, Collation.Binary))
        {
            normalized = collation.Normalize(input);
            offsets = new int[input.Length + 1];
            firstBoundaries = new int[normalized.Length + 1];
            Array.Fill(firstBoundaries, -1);
            firstBoundaries[0] = 0;
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
                if (firstBoundaries[offset] < 0) { firstBoundaries[offset] = index; }
                work++;
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
            Charge(ref work, 1, limit, collation, cancellationToken);
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
                // Only the complete remaining text can satisfy a final literal.
                // PostgreSQL like_match.c:272-286 uses this same terminal shortcut.
                // In particular, an ordinary long exact match needs one comparison.
                if (index + 1 == _tokens.Length)
                {
                    if (collation.IsIndexBacked)
                    {
                        work += token.Length;
                        if (normalized.AsSpan(normalizedStart).SequenceEqual(token.AsSpan())) { return true; }
                    }
                    else
                    {
                        Charge(ref work, Math.Max(1L, (long)input.Length - start + token.Length), limit, collation, cancellationToken);
                        if (CultureInfo.InvariantCulture.CompareInfo.Compare(input.AsSpan(start), token.AsSpan(), CompareOptions.None) == 0) { return true; }
                    }
                    continue;
                }
                // The deterministic transform only appends. Compare the complete run
                // once, then find original boundaries at its normalized end offset.
                int firstEnd = start;
                if (collation.IsIndexBacked)
                {
                    work += token.Length;
                    if (!normalized.AsSpan(normalizedStart).StartsWith(token, StringComparison.Ordinal)) { continue; }
                    // Ignored scalars can leave many raw starts at the same normalized
                    // offset. Jump to the first complete original boundary, rather than
                    // probing that ignored prefix again for each start. A literal cannot
                    // end halfway through one original scalar's expansion.
                    firstEnd = token.Length == 0 ? start : firstBoundaries![normalizedStart + token.Length];
                    if (firstEnd < 0) { continue; }
                }
                for (int end = state.End < 0 ? firstEnd : state.End; end <= input.Length;)
                {
                    if ((steps++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                    Charge(ref work, 1, limit, collation, cancellationToken);
                    bool equal;
                    if (collation.IsIndexBacked)
                    {
                        int length = offsets![end] - normalizedStart;
                        if (length > token.Length) { break; }
                        equal = length == token.Length;
                    }
                    else
                    {
                        // Charge comparison lengths, not just probes: CompareInfo may scan
                        // the complete prefix and literal, even though no string is copied.
                        Charge(ref work, Math.Max(1L, (long)end - start + token.Length), limit, collation, cancellationToken);
                        var prefix = input.AsSpan(start, end - start);
                        var compare = CultureInfo.InvariantCulture.CompareInfo;
                        // Linguistic prefixes can reorder (for example Thai/Lao).
                        // Probe every scalar boundary, as like_match.c:289-311 does.
                        equal = compare.Compare(prefix, token.AsSpan(), CompareOptions.None) == 0;
                    }
                    if (equal)
                    {
                        // An earlier arrival at this suffix already queued its ignored
                        // extensions. Coalesce those continuations as well as suffixes,
                        // so an ignored run cannot be enumerated once per raw start.
                        if (collation.IsIndexBacked && visited[index + 1] is { } suffixes &&
                            (suffixes[end >> 5] & (1u << (end & 31))) != 0) { break; }
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

    // The budget includes pattern compilation's input size and uses 64-bit arithmetic.
    // Index-backed paths keep their O(text * pattern) search; only linguistic probing
    // can exhaust this budget. Cancellation takes precedence over budget refusal.
    internal long WorkLimit(int inputLength) => 64L * ((long)inputLength + _sourceLength + 1);

    private static void Charge(ref long work, long amount, long limit, Collation collation, CancellationToken cancellationToken)
    {
        if (!collation.IsIndexBacked && amount > limit - work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SqlPlanningException("COHSQLE040", ErrorMessages.LikeWorkLimit);
        }
        work += amount;
    }

    private static bool MatchOrdinal(string input, string?[] tokens, CancellationToken cancellationToken, ref long work)
    {
        int position = 0, tokenIndex = 0, star = -1, retry = 0, steps = 0;
        // Iterative backtracking to the last %, bounded stack even for long dynamic patterns.
        while (true)
        {
            if ((steps++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            work++;
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
                work += token.Length;
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

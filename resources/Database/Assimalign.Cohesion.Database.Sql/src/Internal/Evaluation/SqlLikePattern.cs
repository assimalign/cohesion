using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A tokenized LIKE pattern. Escapes and wildcards are read before literal normalization.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlLikePattern")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlLikePattern.DebugView))]
internal sealed class SqlLikePattern
{
    // A null literal is %, an empty literal is _, and a nonempty literal is a literal run.
    private readonly string?[] _tokens;
    private readonly int _sourceLength;
    private readonly bool _requiresBoundaryProbing;
    private readonly bool _hasIgnorables;
    private readonly bool _asciiLetterOrDigitLiterals = true;
    private readonly Collation? _normalizedFor;
    private readonly string?[]? _normalizedTokens;

    private SqlLikePattern(string?[] tokens, int sourceLength, Collation? normalizedFor = null, string?[]? normalizedTokens = null)
    {
        _tokens = tokens;
        foreach (string? literal in tokens)
        {
            if (literal is null) { continue; }
            for (int position = 0; position < literal.Length; position += ScalarLength(literal, position))
            {
                if (!char.IsAsciiLetterOrDigit(literal[position])) { _asciiLetterOrDigitLiterals = false; }
                if (MayBeIgnorable(literal, position)) { _hasIgnorables = true; }
                if (NeedsBoundaryProbing(literal, position)) { _requiresBoundaryProbing = true; }
            }
        }
        _sourceLength = sourceLength;
        _normalizedFor = normalizedFor;
        _normalizedTokens = normalizedTokens;
    }

    internal static void ValidateEscape(string escape)
    {
        if (escape.Length != 0 && (!Rune.TryGetRuneAt(escape, 0, out var rune) || rune.Utf16SequenceLength != escape.Length))
        {
            throw new SqlPlanningException("CS02034", ErrorMessages.LikeEscapeInvalid);
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
                if (index == pattern.Length) { throw new SqlPlanningException("CS02034", ErrorMessages.LikeEscapeInvalid); }
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
    internal bool Match(string input, Collation collation, CancellationToken cancellationToken, out long work, Action? beforeMatch = null)
    {
        work = 0;
        beforeMatch?.Invoke();
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
        bool probeBoundaries = _requiresBoundaryProbing;
        bool hasIgnorables = _hasIgnorables;
        string? simpleLiteral = null;
        bool contains = false;
        try
        {
            if (collation.IsIndexBacked && !ReferenceEquals(collation, Collation.Binary))
            {
                normalized = collation.Normalize(input);
                offsets = ArrayPool<int>.Shared.Rent(checked(input.Length + 1));
                offsets[0] = 0;
                firstBoundaries = ArrayPool<int>.Shared.Rent(checked(normalized.Length + 1));
                firstBoundaries.AsSpan(0, normalized.Length + 1).Fill(-1);
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
                // A terminal independent literal only needs the complete suffix.
                // Earlier text cannot affect its starting boundary supplied by %.
                // Keep this ahead of input analysis, including for combining text
                // and ignored scalars (PostgreSQL like_match.c:272-286).
                if (_tokens.Length == 2 && _tokens[0] is null &&
                    _tokens[1] is { Length: > 0 } terminal && !_requiresBoundaryProbing)
                {
                    Charge(ref work, Math.Max(1L, (long)input.Length + terminal.Length) + 1, limit, collation, cancellationToken);
                    bool suffix = CultureInfo.InvariantCulture.CompareInfo.IsSuffix(input.AsSpan(), terminal, CompareOptions.None);
                    cancellationToken.ThrowIfCancellationRequested();
                    return suffix;
                }
                // A missing independent final suffix rules out every arrival at
                // that literal, before wildcard retries can repeat the same search.
                if (_tokens.Length > 1 && !_requiresBoundaryProbing && _tokens[^1] is { Length: > 0 } tail)
                {
                    Charge(ref work, Math.Max(1L, (long)input.Length + tail.Length) + 1, limit, collation, cancellationToken);
                    bool suffix = CultureInfo.InvariantCulture.CompareInfo.IsSuffix(input.AsSpan(), tail, CompareOptions.None);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!suffix) { return false; }
                }
                // Ordinary single-run ASCII predicates search first. A failed
                // search only needs boundary probing when the input has context.
                if (_asciiLetterOrDigitLiterals)
                {
                    var compare = CultureInfo.InvariantCulture.CompareInfo;
                    if (_tokens.Length == 2 && _tokens[0] is { Length: > 0 } prefix && _tokens[1] is null)
                    {
                        bool found = compare.IsPrefix(input.AsSpan(), prefix, CompareOptions.None, out int matchLength);
                        Charge(ref work, Math.Max(1L, (long)(found ? matchLength : input.Length) + prefix.Length) + 1, limit, collation, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (found) { return true; }
                        simpleLiteral = prefix;
                    }
                    else if (_tokens.Length == 3 && _tokens[0] is null &&
                        _tokens[1] is { Length: > 0 } middle && _tokens[2] is null)
                    {
                        int found = compare.IndexOf(input.AsSpan(), middle, CompareOptions.None, out int matchLength);
                        Charge(ref work, Math.Max(1L, (long)(found < 0 ? input.Length : found + matchLength) + middle.Length) + 1, limit, collation, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (found >= 0) { return true; }
                        simpleLiteral = middle;
                        contains = true;
                    }
                }
                // Search APIs see surrounding collation elements. Combining text and
                // scripts with contextual ordering retain exhaustive scalar probing;
                // independent scripts can use match lengths from collation searches.
                for (int position = 0; !probeBoundaries && _tokens.Length > 1 && position < input.Length; position += ScalarLength(input, position))
                {
                    if ((position & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                    Charge(ref work, 1, limit, collation, cancellationToken);
                    hasIgnorables |= MayBeIgnorable(input, position);
                    probeBoundaries = NeedsBoundaryProbing(input, position);
                }
            }

            if (simpleLiteral is not null)
            {
                return probeBoundaries && MatchAsciiLiteral(input, simpleLiteral, contains, cancellationToken, limit, ref work);
            }

            if (!collation.IsIndexBacked && !probeBoundaries && !hasIgnorables && _tokens.Length > 1)
            {
                return MatchAnchored(input, cancellationToken, limit, ref work);
            }

            // PostgreSQL like_match.c:193-221 advances wildcards over original characters,
            // comparing literal runs separately. Frames and failed suffixes bound repeated work.
            var pending = new Stack<(int Token, int Start, int End)>();
            // Allocate rows lazily: at most one bit per token/input boundary, rather
            // than one hash entry per state on repetitive nonmatching input.
            var visited = new uint[_tokens.Length][];
            var firstVisited = new int[_tokens.Length];
            Array.Fill(firstVisited, -1);
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
                    int first = firstVisited[index];
                    if (first == start) { continue; }
                    if (first < 0) { firstVisited[index] = start; }
                    else
                    {
                        var row = visited[index];
                        if (row is null)
                        {
                            int words = (int)(((long)input.Length + 32) / 32);
                            // One boundary needs only an inline slot. Charge expansion before
                            // allocating, so a simple successful path stays cheap while many
                            // alternative starts cannot bypass the linguistic work budget.
                            Charge(ref work, words, limit, collation, cancellationToken);
                            visited[index] = row = new uint[words];
                            row[first >> 5] |= 1u << (first & 31);
                        }
                        uint mask = 1u << (start & 31);
                        if ((row[start >> 5] & mask) != 0) { continue; }
                        row[start >> 5] |= mask;
                    }
                }
                string? raw = _tokens[index];
                if (raw is null)
                {
                    if (index + 1 == _tokens.Length) { return true; }
                    if (!collation.IsIndexBacked && (!probeBoundaries || index + 2 == _tokens.Length && !_requiresBoundaryProbing) && _tokens[index + 1] is { Length: > 0 } anchor)
                    {
                        var remaining = input.AsSpan(start);
                        var compare = CultureInfo.InvariantCulture.CompareInfo;
                        cancellationToken.ThrowIfCancellationRequested();
                        if (index + 2 == _tokens.Length)
                        {
                            // The terminal literal must cover the remaining suffix, as in
                            // PostgreSQL like_match.c:272-286; % supplies its starting boundary.
                            Charge(ref work, Math.Max(1L, (long)remaining.Length + anchor.Length), limit, collation, cancellationToken);
                            bool suffix = compare.IsSuffix(remaining, anchor, CompareOptions.None);
                            cancellationToken.ThrowIfCancellationRequested();
                            if (suffix) { return true; }
                            continue;
                        }
                        int found = compare.IndexOf(remaining, anchor, CompareOptions.None, out int matchLength);
                        Charge(ref work, Math.Max(1L, (long)(found < 0 ? remaining.Length : found + matchLength) + anchor.Length), limit, collation, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (found < 0) { continue; }
                        start += found;
                        if (start < input.Length) { pending.Push((index, start + ScalarLength(input, start), -1)); }
                        pending.Push((index + 1, start, -1));
                        continue;
                    }
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
                    bool wordLiteral = !collation.IsIndexBacked && (_asciiLetterOrDigitLiterals || IsAsciiWord(token));
                    if (wordLiteral && probeBoundaries && state.End < 0)
                    {
                        if (!TryMatchAsciiPrefix(input, start, token, cancellationToken, limit, ref work, out firstEnd)) { continue; }
                    }
                    if (!collation.IsIndexBacked && !probeBoundaries)
                    {
                        var compare = CultureInfo.InvariantCulture.CompareInfo;
                        cancellationToken.ThrowIfCancellationRequested();
                        bool prefix = compare.IsPrefix(input.AsSpan(start), token, CompareOptions.None, out int matchLength);
                        Charge(ref work, Math.Max(1L, (long)(prefix ? matchLength : input.Length - start) + token.Length), limit, collation, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!prefix) { continue; }
                        firstEnd = start + matchLength;
                    }
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
                        else if (!probeBoundaries || wordLiteral)
                        {
                            // IsPrefix supplies the first candidate end. Only ignored
                            // scalars can extend it without changing equality.
                            int previous = end > 1 && char.IsLowSurrogate(input[end - 1]) && char.IsHighSurrogate(input[end - 2]) ? end - 2 : end - 1;
                            equal = end == firstEnd;
                            if (!equal)
                            {
                                Charge(ref work, end - previous, limit, collation, cancellationToken);
                                equal = CultureInfo.InvariantCulture.CompareInfo.Compare(input.AsSpan(previous, end - previous), ReadOnlySpan<char>.Empty, CompareOptions.None) == 0;
                            }
                            if (!equal) { break; }
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
                            if ((collation.IsIndexBacked || !probeBoundaries || wordLiteral) && (firstVisited[index + 1] == end ||
                                visited[index + 1] is { } suffixes &&
                                (suffixes[end >> 5] & (1u << (end & 31))) != 0)) { break; }
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
        finally
        {
            if (firstBoundaries is not null) { ArrayPool<int>.Shared.Return(firstBoundaries); }
            if (offsets is not null) { ArrayPool<int>.Shared.Return(offsets); }
        }
    }

    // The budget includes pattern compilation's input size and uses 64-bit arithmetic.
    // Index-backed paths keep their O(text * pattern) search; only linguistic probing
    // can exhaust this budget. Cancellation takes precedence over budget refusal.
    internal long WorkLimit(int inputLength) => checked(64L * (inputLength + 1L) * (_sourceLength + 1L));

    private static void Charge(ref long work, long amount, long limit, Collation collation, CancellationToken cancellationToken)
    {
        if (!collation.IsIndexBacked && amount > limit - work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new SqlPlanningException("CS05040", ErrorMessages.LikeWorkLimit);
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

    // Independent scalar ends without ignorables give each literal one candidate
    // end. Last-% backtracking then needs no visited matrix or pending frames.
    private bool MatchAnchored(string input, CancellationToken cancellationToken, long limit, ref long work)
    {
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        int position = 0, index = 0, star = -1, retry = 0;
        if (_tokens[^1] is { Length: > 0 } terminal)
        {
            Charge(ref work, Math.Max(1L, (long)input.Length + terminal.Length), limit, Collation.Invariant, cancellationToken);
            bool suffix = compare.IsSuffix(input, terminal, CompareOptions.None);
            cancellationToken.ThrowIfCancellationRequested();
            if (!suffix) { return false; }
            if (_tokens.Length == 2 && _tokens[0] is null) { return true; }
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Charge(ref work, 1, limit, Collation.Invariant, cancellationToken);
            if (index == _tokens.Length && position == input.Length) { return true; }
            if (index < _tokens.Length)
            {
                string? literal = _tokens[index];
                if (literal is null)
                {
                    if (index + 1 == _tokens.Length) { return true; }
                    star = index++;
                    retry = position;
                    continue;
                }
                if (literal.Length == 0 && position < input.Length)
                {
                    position += ScalarLength(input, position);
                    index++;
                    continue;
                }
                if (literal.Length > 0)
                {
                    bool prefix;
                    int matchLength;
                    if (star >= 0 && index == star + 1)
                    {
                        int found = compare.IndexOf(input.AsSpan(position), literal, CompareOptions.None, out matchLength);
                        Charge(ref work, Math.Max(1L, (long)(found < 0 ? input.Length - position : found + matchLength) + literal.Length), limit, Collation.Invariant, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (found < 0) { return false; }
                        position += found;
                        retry = position;
                        prefix = true;
                    }
                    else
                    {
                        prefix = compare.IsPrefix(input.AsSpan(position), literal, CompareOptions.None, out matchLength);
                        // Independent ends without ignored scalars let a failed prefix
                        // stop within the literal's extent, unlike a substring search.
                        Charge(ref work, Math.Max(1L, (long)(prefix ? matchLength : Math.Min(input.Length - position, literal.Length)) + literal.Length), limit, Collation.Invariant, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    if (prefix)
                    {
                        position += matchLength;
                        index++;
                        continue;
                    }
                }
            }
            if (star < 0 || retry == input.Length) { return false; }
            retry += ScalarLength(input, retry);
            position = retry;
            index = star + 1;
        }
    }

    // ASCII letters and digits have no combining weights or reordered elements.
    // Once an isolated source prefix stops being a prefix of that literal, later
    // scalars cannot repair it. Ignored runs do not change a failed comparison,
    // so skip them rather than comparing the complete run at every boundary.
    private static bool MatchAsciiLiteral(string input, string literal, bool contains,
        CancellationToken cancellationToken, long limit, ref long work)
    {
        if (!contains) { return TryMatchAsciiPrefix(input, 0, literal, cancellationToken, limit, ref work, out _); }
        int start = 0;
        while (true)
        {
            start = SkipIgnored(input, start, cancellationToken, limit, ref work);
            if (start == input.Length) { return false; }
            if (TryMatchAsciiPrefix(input, start, literal, cancellationToken, limit, ref work, out _)) { return true; }
            start += ScalarLength(input, start);
        }
    }

    private static bool TryMatchAsciiPrefix(string input, int start, string literal,
        CancellationToken cancellationToken, long limit, ref long work, out int end)
    {
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        end = SkipIgnored(input, start, cancellationToken, limit, ref work);
        if (end == input.Length) { return false; }
        end += ScalarLength(input, end);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = input.AsSpan(start, end - start);
            Charge(ref work, (long)prefix.Length + literal.Length + 1, limit, Collation.Invariant, cancellationToken);
            if (compare.Compare(prefix, literal, CompareOptions.None) == 0) { return true; }
            Charge(ref work, (long)prefix.Length + literal.Length, limit, Collation.Invariant, cancellationToken);
            if (!compare.IsPrefix(literal, prefix, CompareOptions.None, out _)) { return false; }
            int next = SkipIgnored(input, end, cancellationToken, limit, ref work);
            if (next == input.Length) { return false; }
            end = next + ScalarLength(input, next);
        }
    }

    private static bool IsAsciiWord(string literal)
    {
        foreach (char character in literal)
        {
            if (!char.IsAsciiLetterOrDigit(character)) { return false; }
        }
        return true;
    }

    private static int SkipIgnored(string input, int start, CancellationToken cancellationToken, long limit, ref long work)
    {
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        int steps = 0;
        while (start < input.Length)
        {
            if ((steps++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            int length = ScalarLength(input, start);
            Charge(ref work, length + 1, limit, Collation.Invariant, cancellationToken);
            if (compare.Compare(input.AsSpan(start, length), ReadOnlySpan<char>.Empty, CompareOptions.None) != 0) { break; }
            start += length;
        }
        return start;
    }

    private static bool MayBeIgnorable(string text, int position)
        => text[position] <= 0x7f ? char.IsControl(text[position]) :
            Rune.TryGetRuneAt(text, position, out var scalar) &&
            Rune.GetUnicodeCategory(scalar) is UnicodeCategory.Control or UnicodeCategory.Format;

    private static bool NeedsBoundaryProbing(string text, int position)
    {
        if (text[position] <= 0x7f) { return false; }
        if (!Rune.TryGetRuneAt(text, position, out var scalar)) { return true; }
        // Even in otherwise independent scripts, a mark can change whether a
        // prefix of the whole text compares like the same isolated scalar span.
        if (Rune.GetUnicodeCategory(scalar) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.ModifierLetter) { return true; }
        // Keep contextual scripts (including Thai/Lao reordering, Indic
        // contractions and Hangul Jamo composition) on the scalar-boundary path.
        // Latin, Greek, Cyrillic, Hebrew/Arabic letters, symbols, ideographs,
        // complete Hangul syllables, Deseret and emoji have independent ends.
        int value = scalar.Value;
        return value is not (<= 0x02ff or >= 0x0370 and <= 0x08ff or
            >= 0x1e00 and <= 0x2bff or >= 0x3400 and <= 0x9fff or
            >= 0xac00 and <= 0xd7a3 or >= 0x10400 and <= 0x1044f or
            >= 0x1f000 and <= 0x1faff or >= 0x20000 and <= 0x323af);
    }

    private static int ScalarLength(string text, int position)
        => char.IsHighSurrogate(text[position]) && position + 1 < text.Length && char.IsLowSurrogate(text[position + 1]) ? 2 : 1;

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlLikePattern value)
        {
            _ = value;
        }
    }
}

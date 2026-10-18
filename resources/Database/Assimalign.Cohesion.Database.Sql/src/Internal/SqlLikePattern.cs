using System;
using System.Collections.Generic;
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

    private SqlLikePattern(string?[] tokens, Collation? normalizedFor = null)
    {
        _tokens = tokens;
        _normalizedFor = normalizedFor;
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
        var tokens = new List<string?>(_tokens.Length);
        foreach (string? token in _tokens)
        {
            if (token is null || token.Length == 0) { tokens.Add(token); }
            else
            {
                string normalized = collation.Normalize(token);
                // A literal run stripped entirely by an accent-insensitive collation is empty,
                // never the underscore wildcard represented by an empty token.
                if (normalized.Length != 0) { tokens.Add(normalized); }
            }
        }
        return new SqlLikePattern(tokens.ToArray(), collation);
    }

    internal bool Match(string input, Collation collation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!collation.IsIndexBacked) { return MatchLinguistic(input, collation, cancellationToken); }
        var pattern = Normalize(collation);
        input = collation.Normalize(input);
        int position = 0, tokenIndex = 0, star = -1, retry = 0, steps = 0;
        // Iterative backtracking to the last %, bounded stack even for long dynamic patterns.
        while (true)
        {
            if ((steps++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            if (tokenIndex == pattern._tokens.Length && position == input.Length) { return true; }
            if (tokenIndex < pattern._tokens.Length)
            {
                string? token = pattern._tokens[tokenIndex];
                if (token is null)
                {
                    if (tokenIndex + 1 == pattern._tokens.Length) { return true; }
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

    // Linguistic equality can match a run against prefixes of different UTF-16 lengths.
    // Explicit continuation frames try those prefixes lazily. A visited state prevents
    // repeated wildcard paths from re-exploring an already failed suffix.
    private bool MatchLinguistic(string input, Collation collation, CancellationToken cancellationToken)
    {
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
            string? token = _tokens[index];
            if (token is null)
            {
                if (index + 1 == _tokens.Length) { return true; }
                if (start < input.Length) { pending.Push((index, start + ScalarLength(input, start), -1)); }
                pending.Push((index + 1, start, -1));
            }
            else if (token.Length == 0)
            {
                if (start < input.Length) { pending.Push((index + 1, start + ScalarLength(input, start), -1)); }
            }
            else
            {
                for (int end = state.End < 0 ? start : state.End; end <= input.Length;)
                {
                    if ((steps++ & 1023) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                    if (collation.Compare(input[start..end], token) == 0)
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

    private static int ScalarLength(string text, int position)
        => char.IsHighSurrogate(text[position]) && position + 1 < text.Length && char.IsLowSurrogate(text[position + 1]) ? 2 : 1;
}

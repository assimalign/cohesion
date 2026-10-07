using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// A rule's target (its replacement), parsed once when the rule is registered and expanded per request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Syntax.</b> The target is URL text: a path that starts with <c>/</c> (or with a substitution), an
/// optional <c>?query</c>, and for a redirect an optional <c>#fragment</c>; a redirect may instead target an
/// absolute <c>http://</c> or <c>https://</c> URL. <c>$n</c> and <c>${n}</c> substitute capture group
/// <c>n</c> (<c>$0</c> is the whole match), <c>${name}</c> a named group, and <c>$$</c> is a literal
/// <c>$</c>; a <c>$</c> that starts none of these is literal text. Every reference is checked against the
/// pattern here, so a typo fails at startup rather than per request.
/// </para>
/// <para>
/// <b>Encoding.</b> Literal text is taken as written, with characters a URL cannot carry percent-encoded.
/// A capture is percent-encoded for the part of the URL it lands in: the decoded path the pattern matched
/// keeps <c>/</c> in a path but is fully escaped in a query, fragment or authority, so a capture can never
/// add a query parameter, a fragment or a host, and an already-encoded query capture is inserted as is. The
/// expansion is parsed back once, the way a transport parses a request target, so no value is ever decoded
/// twice.
/// </para>
/// </remarks>
internal sealed class RewriteTemplate
{
    // The characters HttpPath rejects once decoded (and NUL, which the URL decoder rejects).
    private static readonly SearchValues<char> _invalidPathCharacters = SearchValues.Create(" ?#\r\n\t\0");

    private readonly string? _scheme;
    private readonly RewriteTemplateToken[] _authority;
    private readonly RewriteTemplateToken[] _path;
    private readonly RewriteTemplateToken[]? _query;
    private readonly RewriteTemplateToken[]? _fragment;
    private readonly RewriteTarget? _literal;

    private RewriteTemplate(
        string? scheme,
        RewriteTemplateToken[] authority,
        RewriteTemplateToken[] path,
        RewriteTemplateToken[]? query,
        RewriteTemplateToken[]? fragment)
    {
        _scheme = scheme;
        _authority = authority;
        _path = path;
        _query = query;
        _fragment = fragment;

        // A target without substitutions expands to the same text for every request: expand it once.
        if (IsLiteral(authority) && IsLiteral(path) && IsLiteral(query) && IsLiteral(fragment))
        {
            _literal = Expand(null, string.Empty, 0);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the target is an absolute URL.
    /// </summary>
    public bool IsAbsolute => _scheme is not null;

    /// <summary>
    /// Parses a rule target.
    /// </summary>
    /// <param name="template">The target text.</param>
    /// <param name="pattern">The rule's pattern, whose groups the substitutions reference; <see langword="null"/> for a rule without one, whose target cannot substitute.</param>
    /// <param name="redirect">Whether the target is a redirect's, which may be an absolute URL and carry a fragment.</param>
    /// <param name="parameterName">The parameter name to report a malformed target against.</param>
    /// <returns>The parsed target.</returns>
    /// <exception cref="ArgumentException">The target is empty or malformed, or references a group the pattern does not define.</exception>
    public static RewriteTemplate Parse(string template, Regex? pattern, bool redirect, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(template, parameterName);

        List<RewriteTemplateToken> tokens = Tokenize(template, pattern, parameterName);

        string? scheme = null;
        Section section = Section.Path;

        if (tokens[0].Literal is string first)
        {
            if (TryGetScheme(first, out scheme, out int length))
            {
                if (!redirect)
                {
                    throw new ArgumentException(
                        $"The rewrite target '{template}' is an absolute URL. A rewrite changes the path and query of the request; only a redirect can send the client to another origin.",
                        parameterName);
                }

                tokens[0] = RewriteTemplateToken.ForLiteral(first[length..]);
                section = Section.Authority;
            }
            else if (first[0] != '/')
            {
                throw new ArgumentException(
                    redirect
                        ? $"The redirect target '{template}' must start with '/', a substitution, or 'http://' or 'https://'."
                        : $"The rewrite target '{template}' must start with '/' or a substitution.",
                    parameterName);
            }
        }

        List<RewriteTemplateToken> authority = new();
        List<RewriteTemplateToken> path = new();
        List<RewriteTemplateToken>? query = null;
        List<RewriteTemplateToken>? fragment = null;

        foreach (RewriteTemplateToken token in tokens)
        {
            if (!token.IsLiteral)
            {
                Current(section, authority, path, query, fragment).Add(token);
                continue;
            }

            string text = token.Literal!;
            int start = 0;

            for (int i = 0; i < text.Length; i++)
            {
                Section next = NextSection(section, text[i]);
                if (next == section)
                {
                    continue;
                }

                if (next == Section.Fragment && !redirect)
                {
                    throw new ArgumentException(
                        $"The rewrite target '{template}' has a fragment ('#'). A fragment never reaches the server, so a rewrite cannot set one.",
                        parameterName);
                }

                AddLiteral(Current(section, authority, path, query, fragment), text[start..i]);
                section = next;

                if (next == Section.Query)
                {
                    query = new List<RewriteTemplateToken>();
                    start = i + 1;
                }
                else if (next == Section.Fragment)
                {
                    fragment = new List<RewriteTemplateToken>();
                    start = i + 1;
                }
                else
                {
                    // Authority -> path: the '/' starts the path.
                    start = i;
                }
            }

            AddLiteral(Current(section, authority, path, query, fragment), text[start..]);
        }

        if (scheme is not null && authority.Count == 0)
        {
            throw new ArgumentException($"The redirect target '{template}' is an absolute URL without a host.", parameterName);
        }

        RewriteTemplateToken[] normalizedPath = Normalize(path, RewriteUrl.PathCharacters);
        RewriteTemplateToken[]? normalizedQuery = query is null ? null : Normalize(query, RewriteUrl.QueryLiteralCharacters);

        if (!redirect)
        {
            ValidateRewriteTarget(template, normalizedPath, normalizedQuery, parameterName);
        }

        return new RewriteTemplate(
            scheme,
            Normalize(authority, RewriteUrl.AuthorityLiteralCharacters),
            normalizedPath,
            normalizedQuery,
            fragment is null ? null : Normalize(fragment, RewriteUrl.QueryLiteralCharacters));
    }

    /// <summary>
    /// Expands the target for one match.
    /// </summary>
    /// <param name="match">The successful match, or <see langword="null"/> for a target without substitutions.</param>
    /// <param name="input">The text the pattern matched: the decoded path, then <c>?</c> and the re-encoded query when it was matched too.</param>
    /// <param name="pathLength">The length of the path in <paramref name="input"/>; the query, if any, starts after the <c>?</c> at this index.</param>
    /// <returns>The expanded target, as URL text.</returns>
    public RewriteTarget Expand(Match? match, string input, int pathLength)
    {
        if (_literal is RewriteTarget literal)
        {
            return literal;
        }

        string? authority = _scheme is null ? null : Expand(_authority, component: true, match, input, pathLength);
        string path = Expand(_path, component: false, match, input, pathLength);

        // A path target that opens with a substitution may expand without its leading '/'.
        if (_scheme is null && (path.Length == 0 || path[0] != '/'))
        {
            path = "/" + path;
        }

        string? query = _query is null ? null : Expand(_query, component: true, match, input, pathLength);
        string? fragment = _fragment is null ? null : Expand(_fragment, component: true, match, input, pathLength);

        return new RewriteTarget(_scheme, authority, path, query, fragment);
    }

    private static string Expand(RewriteTemplateToken[] tokens, bool component, Match? match, string input, int pathLength)
    {
        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        if (tokens.Length == 1 && tokens[0].IsLiteral)
        {
            return tokens[0].Literal!;
        }

        StringBuilder builder = new(64);

        foreach (RewriteTemplateToken token in tokens)
        {
            if (token.IsLiteral)
            {
                builder.Append(token.Literal);
            }
            else if (match is not null)
            {
                AppendCapture(builder, match.Groups[token.Group], component, input, pathLength);
            }
        }

        return builder.ToString();
    }

    // A capture is copied per the part of the input it came from: the decoded path is escaped for the target
    // part (keeping '/' in a path, escaping everything but unreserved characters elsewhere), the '?' between
    // path and query is escaped, and the query, which is matched re-encoded, is copied as is.
    private static void AppendCapture(StringBuilder builder, Group group, bool component, string input, int pathLength)
    {
        if (!group.Success || group.Length == 0)
        {
            return;
        }

        int start = group.Index;
        int end = start + group.Length;
        SearchValues<char> allowed = component ? RewriteUrl.ComponentCharacters : RewriteUrl.PathCharacters;

        if (start < pathLength)
        {
            RewriteUrl.AppendEscaped(builder, input.AsSpan(start, Math.Min(end, pathLength) - start), allowed);
        }

        if (start <= pathLength && end > pathLength)
        {
            builder.Append("%3F");
        }

        int queryStart = Math.Max(start, pathLength + 1);
        if (end > queryStart)
        {
            builder.Append(input, queryStart, end - queryStart);
        }
    }

    private static List<RewriteTemplateToken> Tokenize(string template, Regex? pattern, string parameterName)
    {
        List<RewriteTemplateToken> tokens = new();
        StringBuilder literal = new();
        int[]? groups = pattern?.GetGroupNumbers();

        for (int i = 0; i < template.Length; i++)
        {
            char character = template[i];

            if (character != '$' || i + 1 == template.Length)
            {
                literal.Append(character);
                continue;
            }

            char next = template[i + 1];

            if (next == '$')
            {
                literal.Append('$');
                i++;
                continue;
            }

            int group;

            if (char.IsAsciiDigit(next))
            {
                int end = i + 1;
                while (end < template.Length && char.IsAsciiDigit(template[end]))
                {
                    end++;
                }

                group = ResolveNumber(template, template[(i + 1)..end], pattern, groups, parameterName);
                i = end - 1;
            }
            else if (next == '{')
            {
                int close = template.IndexOf('}', i + 2);
                if (close < 0)
                {
                    throw new ArgumentException(
                        $"The substitution at position {i} of the target '{template}' has no closing '}}'.",
                        parameterName);
                }

                string name = template[(i + 2)..close];
                group = name.Length != 0 && IsAllDigits(name)
                    ? ResolveNumber(template, name, pattern, groups, parameterName)
                    : ResolveName(template, name, pattern, parameterName);
                i = close;
            }
            else
            {
                // A '$' that starts no substitution is literal text, as it is in .NET replacement patterns.
                literal.Append('$');
                continue;
            }

            if (literal.Length != 0)
            {
                tokens.Add(RewriteTemplateToken.ForLiteral(literal.ToString()));
                literal.Clear();
            }

            tokens.Add(RewriteTemplateToken.ForGroup(group));
        }

        if (literal.Length != 0)
        {
            tokens.Add(RewriteTemplateToken.ForLiteral(literal.ToString()));
        }

        return tokens;
    }

    private static int ResolveNumber(string template, string digits, Regex? pattern, int[]? groups, string parameterName)
    {
        if (pattern is null || groups is null)
        {
            throw NoPattern(template, parameterName);
        }

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || Array.IndexOf(groups, number) < 0)
        {
            throw new ArgumentException(
                $"The target '{template}' references capture group {digits}, which the pattern '{pattern}' does not define. Use '$$' for a literal '$'.",
                parameterName);
        }

        return number;
    }

    private static int ResolveName(string template, string name, Regex? pattern, string parameterName)
    {
        if (pattern is null)
        {
            throw NoPattern(template, parameterName);
        }

        int number = name.Length == 0 ? -1 : pattern.GroupNumberFromName(name);
        if (number < 0)
        {
            throw new ArgumentException(
                $"The target '{template}' references the capture group '{name}', which the pattern '{pattern}' does not define.",
                parameterName);
        }

        return number;
    }

    // A rewrite's literal text that decodes to a character no request path may carry would fail every
    // request the rule matches, and a literal query entry without a key would too: both are configuration
    // errors, reported at registration.
    private static void ValidateRewriteTarget(
        string template,
        RewriteTemplateToken[] path,
        RewriteTemplateToken[]? query,
        string parameterName)
    {
        foreach (RewriteTemplateToken token in path)
        {
            if (token.IsLiteral && Uri.UnescapeDataString(token.Literal!).AsSpan().ContainsAny(_invalidPathCharacters))
            {
                throw new ArgumentException(
                    $"The rewrite target '{template}' has a path no request can carry: it decodes to a space, '?', '#', a control character or NUL.",
                    parameterName);
            }
        }

        if (query is not null && IsLiteral(query))
        {
            string text = query.Length == 0 ? string.Empty : string.Concat(Array.ConvertAll(query, static token => token.Literal!));
            if (!RewriteUrl.TryParseQuery(text, out _))
            {
                throw new ArgumentException(
                    $"The rewrite target '{template}' has a query entry without a key ('=value').",
                    parameterName);
            }
        }
    }

    private static ArgumentException NoPattern(string template, string parameterName) => new(
        $"The target '{template}' substitutes a capture group, but the rule has no pattern to capture one. Use '$$' for a literal '$'.",
        parameterName);

    private static bool TryGetScheme(string literal, out string? scheme, out int length)
    {
        if (literal.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "https";
            length = "https://".Length;
            return true;
        }

        if (literal.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "http";
            length = "http://".Length;
            return true;
        }

        scheme = null;
        length = 0;
        return false;
    }

    private static Section NextSection(Section section, char character) => (section, character) switch
    {
        (Section.Authority, '/') => Section.Path,
        (Section.Authority or Section.Path, '?') => Section.Query,
        (Section.Authority or Section.Path or Section.Query, '#') => Section.Fragment,
        _ => section,
    };

    private static List<RewriteTemplateToken> Current(
        Section section,
        List<RewriteTemplateToken> authority,
        List<RewriteTemplateToken> path,
        List<RewriteTemplateToken>? query,
        List<RewriteTemplateToken>? fragment) => section switch
    {
        Section.Authority => authority,
        Section.Path => path,
        Section.Query => query!,
        _ => fragment!,
    };

    private static void AddLiteral(List<RewriteTemplateToken> tokens, string text)
    {
        if (text.Length != 0)
        {
            tokens.Add(RewriteTemplateToken.ForLiteral(text));
        }
    }

    private static RewriteTemplateToken[] Normalize(List<RewriteTemplateToken> tokens, SearchValues<char> allowed)
    {
        RewriteTemplateToken[] normalized = new RewriteTemplateToken[tokens.Count];

        for (int i = 0; i < tokens.Count; i++)
        {
            RewriteTemplateToken token = tokens[i];
            normalized[i] = token.IsLiteral
                ? RewriteTemplateToken.ForLiteral(RewriteUrl.NormalizeLiteral(token.Literal!, allowed))
                : token;
        }

        return normalized;
    }

    private static bool IsLiteral(RewriteTemplateToken[]? tokens)
    {
        if (tokens is null)
        {
            return true;
        }

        foreach (RewriteTemplateToken token in tokens)
        {
            if (!token.IsLiteral)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllDigits(string text)
    {
        foreach (char character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private enum Section
    {
        Authority,
        Path,
        Query,
        Fragment,
    }
}

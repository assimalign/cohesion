using System;
using System.Globalization;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private bool TryParseTypedLiteral(ref TokenLexer lexer, out SqlLiteralExpression? literal)
    {
        literal = null;
        if (lexer.Current.Type is not (TokenType.Identifier or TokenType.Keyword or TokenType.Function)) { return false; }
        int start = lexer.Current.Position;
        string prefix = CurrentText(ref lexer).ToUpperInvariant();
        var lookahead = lexer;
        if (!MoveTypeLookahead(ref lookahead)) { return false; }
        if (prefix is "X" or "N" or "B" or "E" or "U")
        {
            int prefixEnd = start + prefix.Length;
            if (prefix == "U" && CurrentText(ref lookahead) == "&" && lookahead.Current.Position == prefixEnd)
            {
                prefixEnd++;
                if (!MoveTypeLookahead(ref lookahead)) { return false; }
                prefix = "U&";
            }
            if (lookahead.Current.Type != TokenType.String || lookahead.Current.Position != prefixEnd) { return false; }
            Advance(ref lexer);
            if (prefix == "U&") { Advance(ref lexer); }
            string text = UnquoteStringLiteral(CurrentText(ref lexer));
            int end = lexer.Current.Position + lexer.Current.Value.Length;
            Advance(ref lexer);
            if (prefix is "B" or "E" or "U&")
            {
                AddUnsupportedSurfaceDiagnostic(start, end, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.QueryParserTryParseTypedLiteralVoid, prefix));
                literal = new SqlLiteralExpression("NULL", SqlLiteralType.Null, Location.Create(1, 1, start, end));
                return true;
            }
            literal = MakeTypedLiteral(prefix == "X" ? SqlLiteralType.Binary : SqlLiteralType.String, text, null, start, end);
            return true;
        }
        if (!SqlTypeNames.TryResolve(prefix, null, null, null, out _) && prefix is not ("NATIONAL" or "BIT" or "SMALLDATETIME")) { return false; }
        // Locate the string before committing to the shared type grammar. A bare date/time/interval
        // remains an ordinary column, and a function call cannot swallow another expression.
        while (lookahead.Current.Type != TokenType.String)
        {
            if (lookahead.Current.Type == TokenType.LeftParen)
            {
                int depth = 1;
                while (depth > 0)
                {
                    if (!MoveTypeLookahead(ref lookahead) || lookahead.Current.Type is TokenType.Semicolon or TokenType.Eof) { return false; }
                    if (lookahead.Current.Type == TokenType.LeftParen) { depth++; }
                    else if (lookahead.Current.Type == TokenType.RightParen) { depth--; }
                }
            }
            else if (CurrentText(ref lookahead).ToUpperInvariant() is not ("WITH" or "WITHOUT" or "TIME" or "ZONE" or "PRECISION" or "CHAR" or "CHARACTER" or "VARYING")) { return false; }
            if (!MoveTypeLookahead(ref lookahead)) { return false; }
        }
        int before = _parseDiagnostics.Count;
        string type = ParseDefinitionType(ref lexer, "Typed literal", out var resolved);
        if (lexer.Current.Type != TokenType.String) { return false; }
        string value = UnquoteStringLiteral(CurrentText(ref lexer));
        int literalEnd = lexer.Current.Position + lexer.Current.Value.Length;
        Advance(ref lexer);
        string? qualifier = null;
        if (prefix == "INTERVAL" && SqlTypeModifiers.IsIntervalField(CurrentText(ref lexer)))
        {
            qualifier = ParseIntervalQualifier(ref lexer, "INTERVAL", out var qualifierEnd)["INTERVAL ".Length..];
            literalEnd = qualifierEnd ?? literalEnd;
            if (lexer.Current.Type == TokenType.LeftParen)
            {
                AddUnsupportedSurfaceDiagnostic(start, literalEnd, ErrorMessages.QueryParserTryParseTypedLiteralVoid2);
                SkipParenthesized(ref lexer);
                // Consume the rest of a precision-qualified range, rather than treating TO as an alias.
                if (IsWord(ref lexer, "TO"))
                {
                    Advance(ref lexer);
                    if (SqlTypeModifiers.IsIntervalField(CurrentText(ref lexer))) { Advance(ref lexer); }
                    if (lexer.Current.Type == TokenType.LeftParen) { SkipParenthesized(ref lexer); }
                }
            }
        }
        SqlLiteralType? kind = resolved?.Type switch
        {
            DatabaseType.Date => SqlLiteralType.Date,
            DatabaseType.Time => SqlLiteralType.Time,
            DatabaseType.DateTime => SqlLiteralType.Timestamp,
            DatabaseType.DateTimeOffset => SqlLiteralType.TimestampWithTimeZone,
            DatabaseType.TimeSpan => SqlLiteralType.Interval,
            _ => null,
        };
        if (kind is null)
        {
            // TIME WITH TIME ZONE has a declaration-specific CS00005; literals use the
            // recognized-unsupported diagnostic instead, as do numeric/UUID/JSON prefixes.
            if (type.StartsWith("TIME WITH TIME ZONE", StringComparison.OrdinalIgnoreCase))
            {
                _parseDiagnostics.RemoveRange(before, _parseDiagnostics.Count - before);
            }
            if (_parseDiagnostics.Count == before) { AddUnsupportedSurfaceDiagnostic(start, literalEnd, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.QueryParserTryParseTypedLiteralVoid3, type.ToUpperInvariant())); }
            literal = new SqlLiteralExpression("NULL", SqlLiteralType.Null, Location.Create(1, 1, start, literalEnd));
        }
        else if (_parseDiagnostics.Count > before)
        {
            literal = new SqlLiteralExpression("NULL", SqlLiteralType.Null, Location.Create(1, 1, start, literalEnd));
        }
        else { literal = MakeTypedLiteral(kind.Value, value, qualifier, start, literalEnd); }
        return true;
    }

    private SqlLiteralExpression MakeTypedLiteral(SqlLiteralType type, string text, string? qualifier, int start, int end)
    {
        try
        {
            string canonical = type switch
            {
                SqlLiteralType.Date => SqlLiteralText.ParseDate(text).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                SqlLiteralType.Time => SqlLiteralText.ParseTime(text).ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
                SqlLiteralType.Timestamp => SqlLiteralText.ParseTimestamp(text).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
                SqlLiteralType.TimestampWithTimeZone => SqlLiteralText.ParseTimestampWithTimeZone(text).ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
                SqlLiteralType.Interval => IntervalText(SqlLiteralText.ParseInterval(text, qualifier)),
                SqlLiteralType.Binary => Convert.ToHexString(SqlLiteralText.ParseBinary(text)),
                _ => text,
            };
            return new SqlLiteralExpression(canonical, type, Location.Create(1, 1, start, end));
        }
        catch (NotSupportedException exception) { AddUnsupportedSurfaceDiagnostic(start, end, exception.Message); }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            _parseDiagnostics.Add(new Diagnostic
            {
                Code = "CS00008", Message = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.QueryParserMakeTypedLiteralDiagnostic, type, exception.Message), Start = start, End = end,
                Severity = DiagnosticSeverity.Error, Location = DiagnosticLocation.Absolute,
            });
        }
        return new SqlLiteralExpression("NULL", SqlLiteralType.Null, Location.Create(1, 1, start, end));
    }

    private static string IntervalText(TimeSpan value)
        => (value.Ticks < 0 ? "-PT" : "PT") + (Math.Abs((decimal)value.Ticks) / TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture) + "S";
}

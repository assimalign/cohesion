using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private SqlCopyToExpression ParseCopy(ref TokenLexer lexer)
    {
        int start = lexer.Current.Position;
        Advance(ref lexer);
        if (lexer.Current.Type == TokenType.LeftParen)
        {
            AddUnsupportedSurfaceDiagnostic(start, _lastTokenEnd, "COPY (query) TO STDOUT");
            ConsumeRemaining(ref lexer);
            return CopyRecovery(start);
        }
        var table = ParseUnaliasedTableReference(ref lexer);
        var columns = lexer.Current.Type == TokenType.LeftParen ? ParseCopyColumns(ref lexer) : new List<string>();
        if (IsWord(ref lexer, "FROM"))
        {
            AddUnsupportedSurfaceDiagnostic(start, _lastTokenEnd, "COPY FROM");
            ConsumeRemaining(ref lexer);
            return CopyRecovery(start);
        }
        if (!IsWord(ref lexer, "TO")) { AddExpectedDiagnostic(ref lexer, "TO STDOUT"); }
        else { Advance(ref lexer); }
        if (IsWord(ref lexer, "PROGRAM") || lexer.Current.Type == TokenType.String)
        {
            AddUnsupportedSurfaceDiagnostic(start, _lastTokenEnd, "COPY server file or PROGRAM");
            ConsumeRemaining(ref lexer);
            return CopyRecovery(start);
        }
        if (!IsWord(ref lexer, "STDOUT")) { AddExpectedDiagnostic(ref lexer, "STDOUT"); }
        else { Advance(ref lexer); }

        string format = "text";
        string? delimiter = null, nullString = null, quote = null, escape = null;
        bool header = false, all = false;
        var force = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool with = IsWord(ref lexer, "WITH");
        if (with) { Advance(ref lexer); }
        if (lexer.Current.Type == TokenType.LeftParen)
        {
            Advance(ref lexer);
            while (!IsAtEnd(ref lexer) && lexer.Current.Type != TokenType.RightParen)
            {
                int position = lexer.Current.Position;
                string option = CurrentIdentifierText(ref lexer);
                if (lexer.Current.Type != TokenType.QuotedIdentifier) { option = option.ToLowerInvariant(); }
                Advance(ref lexer);
                if (!seen.Add(option)) { CopyOptionDiagnostic(position, option); }
                switch (option)
                {
                    case "format": format = CopyOptionValue(ref lexer); break;
                    case "delimiter": delimiter = CopyOptionValue(ref lexer); break;
                    case "null": nullString = CopyOptionValue(ref lexer); break;
                    case "quote": quote = CopyOptionValue(ref lexer); break;
                    case "escape": escape = CopyOptionValue(ref lexer); break;
                    case "encoding":
                        string encoding = CopyOptionValue(ref lexer);
                        if (!CopyUtf8Encoding(encoding))
                        { CopyOptionDiagnostic(position, option); }
                        break;
                    case "header":
                        header = true;
                        if (lexer.Current.Type is not (TokenType.Comma or TokenType.RightParen))
                        {
                            string value = CopyOptionValue(ref lexer).ToLowerInvariant();
                            if (value is "false" or "off") { header = false; }
                            else if (value is not ("true" or "on"))
                            {
                                if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int lines) && lines is 0 or 1)
                                { header = lines == 1; }
                                else { CopyOptionDiagnostic(position, option); }
                            }
                        }
                        break;
                    case "force_quote":
                        if (CurrentText(ref lexer) == "*") { all = true; Advance(ref lexer); }
                        else if (lexer.Current.Type == TokenType.LeftParen) { force = ParseCopyColumns(ref lexer); }
                        else { AddExpectedDiagnostic(ref lexer, "* or (columns)"); }
                        break;
                    default:
                        CopyOptionDiagnostic(position, option);
                        if (lexer.Current.Type is not (TokenType.Comma or TokenType.RightParen)) { Advance(ref lexer); }
                        break;
                }
                if (lexer.Current.Type != TokenType.Comma) { break; }
                Advance(ref lexer);
                if (lexer.Current.Type == TokenType.RightParen) { AddExpectedDiagnostic(ref lexer, "a COPY option"); }
            }
            if (seen.Count == 0) { AddExpectedDiagnostic(ref lexer, "a COPY option"); }
            if (lexer.Current.Type != TokenType.RightParen) { AddExpectedDiagnostic(ref lexer, ")"); }
            else { Advance(ref lexer); }
        }
        else if (with) { AddExpectedDiagnostic(ref lexer, "(options)"); }

        if (format == "binary") { AddUnsupportedSurfaceDiagnostic(start, _lastTokenEnd, "COPY FORMAT binary"); }
        else if (format is not ("text" or "csv")) { CopyOptionDiagnostic(start, "FORMAT"); }
        bool csv = format == "csv";
        delimiter ??= csv ? "," : "\t";
        nullString ??= csv ? "" : "\\N";
        quote ??= "\"";
        escape ??= quote;
        // PostgreSQL commands/copy.c:817-877,890-980: defaults, one-byte characters,
        // unsafe text delimiters, CSV-only controls and unambiguous NULL markers.
        if (!CopySingleByte(delimiter) || delimiter is "\r" or "\n" ||
            !csv && "\\.abcdefghijklmnopqrstuvwxyz0123456789".Contains(delimiter, StringComparison.Ordinal))
        { CopyOptionDiagnostic(start, "DELIMITER"); }
        if (!CopySingleByte(quote) || !CopySingleByte(escape) || csv && delimiter == quote ||
            !csv && (seen.Contains("QUOTE") || seen.Contains("ESCAPE") || seen.Contains("FORCE_QUOTE")))
        { CopyOptionDiagnostic(start, "QUOTE/ESCAPE/FORCE_QUOTE"); }
        if (nullString.Contains('\0') || nullString.Contains('\r') || nullString.Contains('\n') ||
            nullString.Contains(delimiter, StringComparison.Ordinal) || csv && nullString.Contains(quote, StringComparison.Ordinal))
        { CopyOptionDiagnostic(start, "NULL"); }
        return new SqlCopyToExpression(table, columns,
            new SqlCopyOptions(csv, delimiter.Length == 0 ? '\t' : delimiter[0], nullString, header,
                quote.Length == 0 ? '"' : quote[0], escape.Length == 0 ? '"' : escape[0], all, force),
            Location.Create(1, 1, start, _lastTokenEnd));
    }

    private List<string> ParseCopyColumns(ref TokenLexer lexer)
    {
        var columns = new List<string>();
        Advance(ref lexer);
        while (true)
        {
            if (!IsNameToken(ref lexer)) { AddExpectedDiagnostic(ref lexer, "a COPY column"); break; }
            columns.Add(CurrentIdentifierText(ref lexer));
            Advance(ref lexer);
            if (lexer.Current.Type != TokenType.Comma) { break; }
            Advance(ref lexer);
        }
        if (lexer.Current.Type == TokenType.RightParen) { Advance(ref lexer); }
        else { AddExpectedDiagnostic(ref lexer, ")"); }
        return columns;
    }

    private string CopyOptionValue(ref TokenLexer lexer)
    {
        string sign = "";
        if (lexer.Current.Type is TokenType.Plus or TokenType.Minus)
        {
            sign = CurrentText(ref lexer);
            Advance(ref lexer);
            if (lexer.Current.Type is not (TokenType.Integer or TokenType.Float))
            { AddExpectedDiagnostic(ref lexer, "a numeric option value"); return ""; }
        }
        if (!IsIdentifierOrKeyword(ref lexer) && lexer.Current.Type is not (TokenType.String or TokenType.Integer or TokenType.Float or TokenType.Asterisk))
        { AddExpectedDiagnostic(ref lexer, "an option value"); return ""; }
        string result = lexer.Current.Type == TokenType.QuotedIdentifier ? CurrentIdentifierText(ref lexer) : UnquoteStringLiteral(CurrentText(ref lexer));
        if (lexer.Current.Type is TokenType.Identifier or TokenType.Keyword or TokenType.Function) { result = result.ToLowerInvariant(); }
        if (lexer.Current.Type == TokenType.Integer && int.TryParse(sign + result, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int number))
        { result = number.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        else { result = sign + result; }
        Advance(ref lexer);
        return result;
    }

    private static bool CopySingleByte(string value) => value.Length == 1 && value[0] is > '\0' and < '\u0080';

    private static bool CopyUtf8Encoding(string name)
    {
        // PostgreSQL common/encnames.c:194-197,519-560 recognizes utf8/unicode after
        // removing ASCII punctuation and folding case; NAMEDATALEN bounds the input.
        if (name.Length is 0 or >= 64) { return false; }
        Span<char> normalized = stackalloc char[63];
        int count = 0;
        foreach (char character in name)
        {
            if (character is '\0' or > '\u007f') { return false; }
            if (char.IsAsciiLetterOrDigit(character))
            { normalized[count++] = character is >= 'A' and <= 'Z' ? (char)(character + 32) : character; }
        }
        return normalized[..count].SequenceEqual("utf8") || normalized[..count].SequenceEqual("unicode");
    }

    private void CopyOptionDiagnostic(int start, string option)
        => _parseDiagnostics.Add(new Diagnostic { Code = "CS01068", Message = string.Format(System.Globalization.CultureInfo.CurrentCulture,
            ErrorMessages.CopyOptionInvalid, option), Severity = DiagnosticSeverity.Error, Start = start, End = _lastTokenEnd,
            Location = DiagnosticLocation.Absolute });

    private SqlCopyToExpression CopyRecovery(int start) => new(new SqlTableReference("?", null, null), [],
        new SqlCopyOptions(false, '\t', "\\N", false, '"', '"', false, []), Location.Create(1, 1, start, _lastTokenEnd));
}

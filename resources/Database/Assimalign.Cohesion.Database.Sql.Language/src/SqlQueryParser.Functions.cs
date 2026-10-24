using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private SqlExpression ParseNiladic(ref TokenLexer lexer, string name, int position)
    {
        if (lexer.Current.Type == TokenType.LeftParen)
        {
            var next = lexer;
            while (next.MoveNext() && next.Current.Type == TokenType.Comment) { }
            if (next.Current.Type == TokenType.Integer && TryPeekToken(next, out string closing, out _) && closing == ")" &&
                !name.Equals("CURRENT_DATE", StringComparison.OrdinalIgnoreCase))
            {
                AddUnsupportedSurfaceDiagnostic(position, next.Current.Position + next.Current.Value.Length,
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlQueryParserParseNiladicVoid, name.ToUpperInvariant()));
            }
            else { AddSyntaxDiagnostic(ref lexer, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlQueryParserParseNiladicVoid2, name.ToUpperInvariant())); }
            SkipParenthesized(ref lexer);
        }
        return new SqlFunctionCallExpression(name, [], Location.Create(1, 1, position, position + name.Length));
    }

    // PostgreSQL gram.y:16249-16371, 17203ff. Separators are grammar, never ordinary comma-call recovery.
    private SqlExpression ParseStandardForm(ref TokenLexer lexer, string name, int position)
    {
        Advance(ref lexer);
        var arguments = new List<SqlExpression>();
        if (name == "EXTRACT")
        {
            string field = CurrentText(ref lexer).ToUpperInvariant();
            if (field is not ("YEAR" or "QUARTER" or "MONTH" or "WEEK" or "DAY" or "DOW" or "DOY" or "HOUR" or "MINUTE" or "SECOND" or "EPOCH"))
            {
                AddSyntaxDiagnostic(ref lexer, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlQueryParserParseStandardFormVoid, field));
            }
            arguments.Add(new SqlLiteralExpression(field, SqlLiteralType.String, Location.Create(1, 1, lexer.Current.Position, CurrentTokenEnd(ref lexer))));
            Advance(ref lexer);
            ExpectWord(ref lexer, "FROM");
            arguments.Add(ParseOperand(ref lexer, OperandRule.Expression));
        }
        else if (name == "POSITION")
        {
            arguments.Add(ParseOperand(ref lexer, OperandRule.Concat));
            ExpectWord(ref lexer, "IN");
            arguments.Add(ParseOperand(ref lexer, OperandRule.Expression));
        }
        else if (name == "SUBSTRING")
        {
            arguments.Add(ParseOperand(ref lexer, OperandRule.Expression));
            if (lexer.Current.Type == TokenType.Comma)
            {
                Advance(ref lexer);
                arguments.Add(ParseOperand(ref lexer, OperandRule.Expression));
                if (lexer.Current.Type == TokenType.Comma)
                {
                    Advance(ref lexer);
                    arguments.Add(ParseOperand(ref lexer, OperandRule.Expression));
                }
            }
            else
            {
                ExpectWord(ref lexer, "FROM");
                arguments.Add(ParseOperand(ref lexer, OperandRule.Expression));
                if (IsWord(ref lexer, "FOR"))
                {
                    Advance(ref lexer);
                    arguments.Add(ParseOperand(ref lexer, OperandRule.Expression));
                }
            }
        }
        else
        {
            bool direction = IsWord(ref lexer, "LEADING") || IsWord(ref lexer, "TRAILING") || IsWord(ref lexer, "BOTH");
            if (direction)
            {
                name = IsWord(ref lexer, "LEADING") ? "LTRIM" : IsWord(ref lexer, "TRAILING") ? "RTRIM" : "TRIM";
                Advance(ref lexer);
            }
            SqlExpression? characters = null;
            if (IsWord(ref lexer, "FROM"))
            {
                Advance(ref lexer);
                arguments.Add(ParseOperand(ref lexer, OperandRule.Expression));
            }
            else
            {
                var first = ParseOperand(ref lexer, OperandRule.Expression);
                if (IsWord(ref lexer, "FROM"))
                {
                    characters = first;
                    Advance(ref lexer);
                    arguments.Add(ParseOperand(ref lexer, OperandRule.Expression));
                }
                else if (direction)
                {
                    ExpectWord(ref lexer, "FROM");
                    arguments.Add(first);
                }
                else
                {
                    arguments.Add(first);
                    if (lexer.Current.Type == TokenType.Comma)
                    {
                        Advance(ref lexer);
                        characters = ParseOperand(ref lexer, OperandRule.Expression);
                    }
                }
            }
            if (characters is not null) { arguments.Add(characters); }
        }
        Expect(ref lexer, TokenType.RightParen, string.Format(System.Globalization.CultureInfo.CurrentCulture, _errorFunctionClosingParenthesisExpected, name));
        return new SqlFunctionCallExpression(name, arguments, Location.Create(1, 1, position, position + name.Length));
    }

    private void ExpectWord(ref TokenLexer lexer, string word)
    {
        if (IsWord(ref lexer, word)) { Advance(ref lexer); }
        else { AddSyntaxDiagnostic(ref lexer, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlQueryParserExpectWordVoid, word)); }
    }
}

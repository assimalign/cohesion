using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private string ParseDefinitionName(ref TokenLexer lexer, string subject)
    {
        if (!IsNameToken(ref lexer))
        {
            AddExpectedDiagnostic(ref lexer, subject);
            return "?";
        }
        string name = CurrentIdentifierText(ref lexer);
        Advance(ref lexer);
        return name;
    }

    private string ParseDefinitionType(ref TokenLexer lexer)
    {
        if (!IsIdentifierOrKeyword(ref lexer))
        {
            AddExpectedDiagnostic(ref lexer, "a type name");
            return "?";
        }
        // Type text retains lexical delimiters so a quoted '(' cannot become a type argument.
        string type = CurrentText(ref lexer);
        Advance(ref lexer);
        return lexer.Current.Type == TokenType.LeftParen ? type + ParseTypeArguments(ref lexer) : type;
    }

    private SqlCreateFunctionExpression ParseCreateFunction(ref TokenLexer lexer, int pos)
    {
        Advance(ref lexer);
        string name = ParseDefinitionName(ref lexer, "a function name");
        var parameters = new List<SqlFunctionParameter>();
        if (Expect(ref lexer, TokenType.LeftParen, "'(' before function parameters"))
        {
            if (lexer.Current.Type != TokenType.RightParen)
            {
                do
                {
                    if (parameters.Count > 0)
                    {
                        Advance(ref lexer);
                    }
                    string parameter = ParseDefinitionName(ref lexer, "a parameter name");
                    parameters.Add(new SqlFunctionParameter(parameter, ParseDefinitionType(ref lexer)));
                } while (lexer.Current.Type == TokenType.Comma);
            }
            ExpectDdlToken(ref lexer, TokenType.RightParen, "')' after function parameters");
        }
        ExpectKeyword(ref lexer, "RETURNS", "RETURNS and a result type");
        string returnType = ParseDefinitionType(ref lexer);
        string volatility = "VOLATILE";
        bool strict = false, sawVolatility = false, sawNullBehavior = false;
        while (!IsAtEnd(ref lexer) && !IsKeyword(ref lexer, "RETURN"))
        {
            if (IsKeyword(ref lexer, "IMMUTABLE") || IsKeyword(ref lexer, "STABLE") || IsKeyword(ref lexer, "VOLATILE"))
            {
                if (sawVolatility)
                {
                    AddSyntaxDiagnostic(ref lexer, "Function volatility may be declared only once.");
                }
                sawVolatility = true;
                volatility = CurrentText(ref lexer).ToUpperInvariant();
                Advance(ref lexer);
            }
            else if (IsKeyword(ref lexer, "STRICT") || IsKeyword(ref lexer, "CALLED"))
            {
                if (sawNullBehavior)
                {
                    AddSyntaxDiagnostic(ref lexer, "Function null behavior may be declared only once.");
                }
                sawNullBehavior = true;
                strict = IsKeyword(ref lexer, "STRICT");
                Advance(ref lexer);
                if (!strict)
                {
                    ExpectKeyword(ref lexer, "ON", "ON after CALLED");
                    ExpectKeyword(ref lexer, "NULL", "NULL after CALLED ON");
                    ExpectKeyword(ref lexer, "INPUT", "INPUT after CALLED ON NULL");
                }
            }
            else
            {
                break;
            }
        }
        ExpectKeyword(ref lexer, "RETURN", "RETURN and one expression (SQL expression bodies only)");
        var body = ParseExpression(ref lexer);
        return new SqlCreateFunctionExpression(name, parameters, returnType, volatility, strict, body,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }

    private SqlDropFunctionExpression ParseDropFunction(ref TokenLexer lexer, int pos)
    {
        Advance(ref lexer);
        string name = ParseDefinitionName(ref lexer, "a function name");
        var types = new List<string>();
        if (Expect(ref lexer, TokenType.LeftParen, "'(' before the function signature"))
        {
            if (lexer.Current.Type != TokenType.RightParen)
            {
                do
                {
                    if (types.Count > 0)
                    {
                        Advance(ref lexer);
                    }
                    types.Add(ParseDefinitionType(ref lexer));
                } while (lexer.Current.Type == TokenType.Comma);
            }
            ExpectDdlToken(ref lexer, TokenType.RightParen, "')' after the function signature");
        }
        return new SqlDropFunctionExpression(name, types, Location.Create(1, 1, pos, _lastTokenEnd));
    }

    private SqlCreateDomainExpression ParseCreateDomain(ref TokenLexer lexer, int pos)
    {
        Advance(ref lexer);
        string name = ParseDefinitionName(ref lexer, "a domain name");
        ExpectKeyword(ref lexer, "AS", "AS and a base type");
        string type = ParseDefinitionType(ref lexer);
        SqlExpression? defaultValue = null, check = null;
        string? constraint = null;
        bool notNull = false;
        if (IsKeyword(ref lexer, "DEFAULT"))
        {
            Advance(ref lexer);
            defaultValue = ParseUnary(ref lexer);
        }
        if (IsKeyword(ref lexer, "NOT"))
        {
            Advance(ref lexer);
            ExpectKeyword(ref lexer, "NULL", "NULL after NOT");
            notNull = true;
        }
        if (IsKeyword(ref lexer, "CONSTRAINT"))
        {
            Advance(ref lexer);
            constraint = ParseDefinitionName(ref lexer, "a constraint name");
        }
        if (constraint is not null || IsKeyword(ref lexer, "CHECK"))
        {
            ExpectKeyword(ref lexer, "CHECK", "CHECK after a domain constraint name");
            if (Expect(ref lexer, TokenType.LeftParen, "'(' before the domain CHECK"))
            {
                check = ParseExpression(ref lexer);
                ExpectDdlToken(ref lexer, TokenType.RightParen, "')' after the domain CHECK");
            }
        }
        return new SqlCreateDomainExpression(name, type, defaultValue, notNull, constraint, check,
            Location.Create(1, 1, pos, _lastTokenEnd));
    }

    private SqlDropDomainExpression ParseDropDomain(ref TokenLexer lexer, int pos)
    {
        Advance(ref lexer);
        string name = ParseDefinitionName(ref lexer, "a domain name");
        return new SqlDropDomainExpression(name, Location.Create(1, 1, pos, _lastTokenEnd));
    }
}

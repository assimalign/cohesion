using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;
using Assimalign.Cohesion.Database.Types;

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

    private string ParseDefinitionType(ref TokenLexer lexer, string subject)
        => ParseDefinitionType(ref lexer, subject, out _);

    private string ParseDefinitionType(ref TokenLexer lexer, string subject, out DatabaseTypeInfo? resolvedType)
    {
        resolvedType = null;
        if (!IsIdentifierOrKeyword(ref lexer))
        {
            AddExpectedDiagnostic(ref lexer, "a type name");
            return "?";
        }
        int start = lexer.Current.Position;
        int diagnosticsBeforeType = _parseDiagnostics.Count;
        string type = CurrentText(ref lexer);
        Advance(ref lexer);
        type = ParseTypeNameWords(ref lexer, type);
        type = ParseIntervalQualifier(ref lexer, type, out _);
        string arguments = lexer.Current.Type == TokenType.LeftParen
            ? ParseTypeArguments(ref lexer, subject, type) : string.Empty;
        // ISO temporal precision precedes the time-zone suffix. Also recognize the suffix
        // before arguments, so every spelling receives the same modifier diagnostic.
        type = ParseTimeZoneWords(ref lexer, type);
        type += arguments;
        if (_parseDiagnostics.Count == diagnosticsBeforeType &&
            SqlTypeModifiers.TrySplit(type, out string name, out int? first, out int? second))
        {
            if (SqlTypeModifiers.GetError(name, first, second, out string code, isDelimited: type.StartsWith('"')) is { } error)
            {
                AddTypeModifierDiagnostic(subject, error, start, _lastTokenEnd, code);
            }
            else if (SqlTypeNames.TryResolve(name, first, null, second, out var builtIn))
            {
                resolvedType = builtIn;
            }
        }
        if (IsWord(ref lexer, "ARRAY") || CurrentText(ref lexer) == "[")
        {
            int arrayStart = lexer.Current.Position;
            if (IsWord(ref lexer, "ARRAY")) { Advance(ref lexer); }
            while (CurrentText(ref lexer) == "[")
            {
                Advance(ref lexer);
                Expect(ref lexer, TokenType.RightBracket, "']' after the array type");
            }
            AddTypeModifierDiagnostic(subject, ErrorMessages.SqlQueryParserParseDefinitionTypeVoid, arrayStart, _lastTokenEnd, "CK00001");
            resolvedType = null;
        }
        return type;
    }

    // Contextual words: no new keyword reservation and no consumption of column constraints.
    private string ParseTypeNameWords(ref TokenLexer lexer, string type)
    {
        if (type.Equals("DOUBLE", StringComparison.OrdinalIgnoreCase) && IsWord(ref lexer, "PRECISION"))
        {
            return AppendTypeWord(ref lexer, type);
        }
        // PostgreSQL src/backend/parser/gram.y:15130-15140 gives every national name opt_varying.
        if (type.Equals("NATIONAL", StringComparison.OrdinalIgnoreCase) &&
            (IsWord(ref lexer, "CHAR") || IsWord(ref lexer, "CHARACTER")))
        {
            type = AppendTypeWord(ref lexer, type);
        }
        if ((type.Equals("CHAR", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("CHARACTER", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("NCHAR", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("NATIONAL CHAR", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("NATIONAL CHARACTER", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("BINARY", StringComparison.OrdinalIgnoreCase)) && IsWord(ref lexer, "VARYING"))
        {
            return AppendTypeWord(ref lexer, type);
        }
        return ParseTimeZoneWords(ref lexer, type);
    }

    private string ParseTimeZoneWords(ref TokenLexer lexer, string type)
    {
        if ((type.Equals("TIME", StringComparison.OrdinalIgnoreCase) || type.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase)) &&
            (IsWord(ref lexer, "WITH") || IsWord(ref lexer, "WITHOUT")))
        {
            type = AppendTypeWord(ref lexer, type);
            if (!IsWord(ref lexer, "TIME")) { AddExpectedDiagnostic(ref lexer, "TIME in the time-zone type name"); return type; }
            type = AppendTypeWord(ref lexer, type);
            if (!IsWord(ref lexer, "ZONE")) { AddExpectedDiagnostic(ref lexer, "ZONE in the time-zone type name"); return type; }
            type = AppendTypeWord(ref lexer, type);
        }
        return type;
    }

    private string AppendTypeWord(ref TokenLexer lexer, string type)
    {
        type += " " + CurrentText(ref lexer);
        Advance(ref lexer);
        return type;
    }

    private static bool TrySkipTimeZoneType(ref TokenLexer lexer)
    {
        if (!IsWord(ref lexer, "TIMESTAMP") && !IsWord(ref lexer, "TIME")) { return false; }
        var lookahead = lexer;
        if (!MoveTypeLookahead(ref lookahead)) { return false; }
        if (lookahead.Current.Type == TokenType.LeftParen)
        {
            // Only locate the suffix here; ParseTypeArguments owns modifier validation.
            int depth = 1;
            do
            {
                if (!MoveTypeLookahead(ref lookahead) || lookahead.Current.Type == TokenType.Semicolon) { return false; }
                if (lookahead.Current.Type == TokenType.LeftParen) { depth++; }
                else if (lookahead.Current.Type == TokenType.RightParen) { depth--; }
            }
            while (depth > 0);
            if (!MoveTypeLookahead(ref lookahead)) { return false; }
        }
        if (!IsWord(ref lookahead, "WITH") || !MoveTypeLookahead(ref lookahead) ||
            !IsWord(ref lookahead, "TIME") || !MoveTypeLookahead(ref lookahead) ||
            !IsWord(ref lookahead, "ZONE")) { return false; }
        lexer = lookahead;
        return true;
    }

    private static bool MoveTypeLookahead(ref TokenLexer lexer)
    {
        while (lexer.MoveNext())
        {
            if (lexer.Current.Type != TokenType.Comment) { return true; }
        }
        return false;
    }

    private string ParseIntervalQualifier(ref TokenLexer lexer, string type, out int? end)
    {
        end = null;
        if (!type.Equals("INTERVAL", StringComparison.OrdinalIgnoreCase) ||
            !SqlTypeModifiers.IsIntervalField(CurrentText(ref lexer)))
        {
            return type;
        }
        string first = CurrentText(ref lexer);
        type += " " + first;
        end = lexer.Current.Position + lexer.Current.Value.Length;
        Advance(ref lexer);
        if (IsWord(ref lexer, "TO"))
        {
            type += " " + CurrentText(ref lexer);
            Advance(ref lexer);
            string last = CurrentText(ref lexer);
            if (!SqlTypeModifiers.IsIntervalField(last))
            {
                AddExpectedDiagnostic(ref lexer, "an interval field after TO");
                return type;
            }
            if (!SqlTypeModifiers.IsIntervalRange(first, last))
            {
                AddSyntaxDiagnostic(ref lexer, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.QueryParserParseIntervalQualifierVoid, first, last));
            }
            type += " " + last;
            end = lexer.Current.Position + lexer.Current.Value.Length;
            Advance(ref lexer);
        }
        return type;
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
                    parameters.Add(new SqlFunctionParameter(parameter, ParseDefinitionType(ref lexer, $"Function '{name}' parameter '{parameter}'")));
                } while (lexer.Current.Type == TokenType.Comma);
            }
            ExpectDdlToken(ref lexer, TokenType.RightParen, "')' after function parameters");
        }
        ExpectKeyword(ref lexer, "RETURNS", "RETURNS and a result type");
        string returnType = ParseDefinitionType(ref lexer, $"Function '{name}' return type");
        string volatility = "VOLATILE";
        bool strict = false, sawVolatility = false, sawNullBehavior = false;
        while (!IsAtEnd(ref lexer) && !IsKeyword(ref lexer, "RETURN"))
        {
            if (IsKeyword(ref lexer, "IMMUTABLE") || IsKeyword(ref lexer, "STABLE") || IsKeyword(ref lexer, "VOLATILE"))
            {
                if (sawVolatility)
                {
                    AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseCreateFunctionVoid);
                }
                sawVolatility = true;
                volatility = CurrentText(ref lexer).ToUpperInvariant();
                Advance(ref lexer);
            }
            else if (IsKeyword(ref lexer, "STRICT") || IsKeyword(ref lexer, "CALLED"))
            {
                if (sawNullBehavior)
                {
                    AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseCreateFunctionVoid2);
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
                    types.Add(ParseDefinitionType(ref lexer, $"Function '{name}' parameter type"));
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
        if (name.ToUpperInvariant() is "SERIAL" or "BIGSERIAL" or "SMALLSERIAL")
        {
            string? error = SqlTypeModifiers.GetError(name, null, null, out string code);
            AddTypeModifierDiagnostic($"Domain '{name}'", error!, pos, _lastTokenEnd, code);
        }
        ExpectKeyword(ref lexer, "AS", "AS and a base type");
        string type = ParseDefinitionType(ref lexer, $"Domain '{name}'");
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

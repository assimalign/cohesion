using System.Globalization;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private SqlExpression ParseCast(ref TokenLexer lexer)
    {
        int start = lexer.Current.Position;
        if (!Supports(SqlClauses.Cast))
        {
            _parseDiagnostics.Add(QueryDiagnostics.UnsupportedClause(SqlClauses.Cast, Profile.Language,
                Location.Create(1, 1, start, start + lexer.Current.Value.Length)));
        }
        Advance(ref lexer);

        if (lexer.Current.Type == TokenType.LeftParen)
        {
            Advance(ref lexer);
        }
        else
        {
            AddSyntaxDiagnostic(ref lexer, "Expected '(' after CAST.");
        }

        var operand = ParseOperand(ref lexer, OperandRule.Expression);
        if (IsKeyword(ref lexer, "AS"))
        {
            Advance(ref lexer);
        }
        else
        {
            AddSyntaxDiagnostic(ref lexer, "Expected AS before the CAST target type.");
        }

        int targetStart = lexer.Current.Position;
        int targetEnd = targetStart;
        string targetName = string.Empty;
        int? firstArgument = null;
        int? secondArgument = null;
        bool validArguments = true;
        if (IsIdentifierOrKeyword(ref lexer))
        {
            targetName = CurrentText(ref lexer);
            targetEnd = lexer.Current.Position + lexer.Current.Value.Length;
            Advance(ref lexer);
            if (lexer.Current.Type == TokenType.LeftParen)
            {
                Advance(ref lexer);
                validArguments = TryReadCastArgument(ref lexer, out firstArgument);
                if (validArguments && lexer.Current.Type == TokenType.Comma)
                {
                    Advance(ref lexer);
                    validArguments = TryReadCastArgument(ref lexer, out secondArgument);
                }

                if (lexer.Current.Type != TokenType.RightParen)
                {
                    validArguments = false;
                    while (!IsAtEnd(ref lexer) && lexer.Current.Type is not TokenType.RightParen and not TokenType.Semicolon)
                    {
                        Advance(ref lexer);
                    }
                }

                targetEnd = lexer.Current.Position + lexer.Current.Value.Length;
                if (lexer.Current.Type == TokenType.RightParen)
                {
                    Advance(ref lexer);
                }
            }
        }
        else
        {
            AddSyntaxDiagnostic(ref lexer, "Expected a CAST target type name.");
        }

        string targetType = targetName.Length == 0 ? string.Empty : _sourceText[targetStart..targetEnd];
        DatabaseTypeInfo? targetTypeInfo = null;
        if (targetName.Length > 0)
        {
            targetTypeInfo = ResolveCastTarget(targetName, firstArgument, secondArgument,
                validArguments, targetStart, targetEnd);
        }

        if (lexer.Current.Type == TokenType.RightParen)
        {
            Advance(ref lexer);
        }
        else
        {
            AddSyntaxDiagnostic(ref lexer, "Expected ')' after the CAST target type.");
        }

        return new SqlCastExpression(operand, targetType, targetTypeInfo,
            Location.Create(1, 1, start, _lastTokenEnd));
    }

    private bool TryReadCastArgument(ref TokenLexer lexer, out int? argument)
    {
        argument = null;
        if (lexer.Current.Type != TokenType.Integer ||
            !int.TryParse(lexer.Current.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            return false;
        }

        argument = value;
        Advance(ref lexer);
        return true;
    }

    /// <summary>
    /// Resolves a CAST target that is a built-in type name, checking its type arguments. A name
    /// that is not a built-in type is left unresolved without a diagnostic: the engine's type
    /// catalog may register it as a domain (phase E3), so the planner resolves it, and reports a
    /// name it does not know. Which conversions exist is the engine's cast registry's question,
    /// not the parser's, so every built-in type is a valid target here.
    /// </summary>
    private DatabaseTypeInfo? ResolveCastTarget(
        string name, int? firstArgument, int? secondArgument, bool validArguments, int start, int end)
    {
        const string argumentShape = "CAST type arguments must be one or two unsigned integer literals within the Int32 range.";
        if (!SqlTypeNames.TryResolve(name, firstArgument, null, secondArgument, out var typeInfo))
        {
            if (!validArguments)
            {
                AddCastTargetDiagnostic("SQL0005", argumentShape, start, end);
            }

            return null;
        }

        string? error = !validArguments
            ? argumentShape
            : typeInfo.Type switch
            {
                DatabaseType.Decimal when firstArgument is < 1 or > 28 =>
                    "CAST decimal precision must be between 1 and 28.",
                DatabaseType.Decimal when secondArgument > firstArgument =>
                    "CAST decimal scale must be between 0 and precision.",
                DatabaseType.String when firstArgument is < 1 || secondArgument is not null =>
                    "CAST string targets accept one positive length argument.",
                DatabaseType.Binary when firstArgument is < 1 || secondArgument is not null =>
                    "CAST binary targets accept one positive length argument.",
                DatabaseType.Decimal or DatabaseType.String or DatabaseType.Binary => null,
                _ when firstArgument is not null => $"CAST target '{name}' does not accept type arguments.",
                _ => null,
            };

        if (error is not null)
        {
            AddCastTargetDiagnostic("SQL0005", error, start, end);
            return null;
        }

        // An explicit precision implies scale zero; a bare DECIMAL remains unconstrained.
        return typeInfo.Type == DatabaseType.Decimal && typeInfo.Precision is not null
            ? new DatabaseTypeInfo(typeInfo.Type, precision: typeInfo.Precision, scale: typeInfo.Scale ?? 0)
            : typeInfo;
    }

    private void AddCastTargetDiagnostic(string code, string message, int start, int end)
    {
        _parseDiagnostics.Add(new Diagnostic
        {
            Code = code,
            Message = message,
            Start = start,
            End = end,
            Severity = DiagnosticSeverity.Error,
            Location = DiagnosticLocation.Absolute,
        });
    }
}

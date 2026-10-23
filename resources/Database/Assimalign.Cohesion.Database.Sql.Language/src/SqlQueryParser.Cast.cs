using System.Globalization;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language.Internal;
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
            AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseCastVoid);
        }

        var operand = ParseOperand(ref lexer, OperandRule.Expression);
        if (IsKeyword(ref lexer, "AS"))
        {
            Advance(ref lexer);
        }
        else
        {
            AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseCastVoid2);
        }

        string targetType = string.Empty;
        DatabaseTypeInfo? targetTypeInfo = null;
        if (IsIdentifierOrKeyword(ref lexer))
        {
            targetType = ParseDefinitionType(ref lexer, $"CAST target '{CurrentText(ref lexer)}'", out targetTypeInfo);
        }
        else
        {
            AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseCastVoid3);
        }

        if (lexer.Current.Type == TokenType.RightParen)
        {
            Advance(ref lexer);
        }
        else
        {
            AddSyntaxDiagnostic(ref lexer, ErrorMessages.SqlQueryParserParseCastVoid4);
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

    private void AddTypeModifierDiagnostic(string subject, string message, int start, int end,
        string code = SqlTypeModifiers.DiagnosticCode)
    {
        _parseDiagnostics.Add(new Diagnostic
        {
            Code = code,
            Message = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.AddTypeModifierDiagnosticDiagnostic, subject, message),
            Start = start,
            End = end,
            Severity = DiagnosticSeverity.Error,
            Location = DiagnosticLocation.Absolute,
        });
    }
}

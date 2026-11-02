using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language;

public sealed partial class SqlQueryParser
{
    private SqlExpression ParseJsonFunction(ref TokenLexer lexer, string name, int start)
    {
        var kind = name == "JSON_VALUE" ? SqlJsonFunctionKind.Value : name == "JSON_QUERY" ? SqlJsonFunctionKind.Query : SqlJsonFunctionKind.Exists;
        RequireSchemaClause(ref lexer, kind == SqlJsonFunctionKind.Value ? SqlClauses.JsonValue : kind == SqlJsonFunctionKind.Query ? SqlClauses.JsonQuery : SqlClauses.JsonExists);
        Advance(ref lexer);
        var context = ParseOperand(ref lexer, OperandRule.Expression);
        Expect(ref lexer, TokenType.Comma, ",");
        var path = ParseOperand(ref lexer, OperandRule.Expression);
        if (IsWord(ref lexer, "PASSING"))
        {
            _parseDiagnostics.Add(new Diagnostic
            {
                Code = "CS02081", Message = ErrorMessages.JsonPassingUnsupported,
                Start = lexer.Current.Position, End = CurrentTokenEnd(ref lexer),
                Severity = DiagnosticSeverity.Error, Location = DiagnosticLocation.Absolute,
            });
            int nested = 0;
            while (!IsAtEnd(ref lexer))
            {
                if (lexer.Current.Type == TokenType.RightParen && nested == 0) { Advance(ref lexer); break; }
                if (lexer.Current.Type == TokenType.Semicolon && nested == 0) { break; }
                if (lexer.Current.Type == TokenType.LeftParen) { nested++; }
                else if (lexer.Current.Type == TokenType.RightParen) { nested--; }
                Advance(ref lexer);
            }
            return new SqlLiteralExpression("NULL", SqlLiteralType.Null, Location.Create(1, 1, start, _lastTokenEnd));
        }
        string? returning = null;
        DatabaseTypeInfo? returningType = null;
        if (kind != SqlJsonFunctionKind.Exists && IsWord(ref lexer, "RETURNING"))
        {
            Advance(ref lexer);
            returning = ParseDefinitionType(ref lexer, "RETURNING", out returningType);
        }
        var wrapper = SqlJsonWrapper.Without;
        bool omit = false;
        if (kind == SqlJsonFunctionKind.Query)
        {
            if (IsWord(ref lexer, "WITH") || IsWord(ref lexer, "WITHOUT"))
            {
                bool with = IsWord(ref lexer, "WITH");
                Advance(ref lexer);
                wrapper = with ? SqlJsonWrapper.Unconditional : SqlJsonWrapper.Without;
                if (with && (IsWord(ref lexer, "CONDITIONAL") || IsWord(ref lexer, "UNCONDITIONAL")))
                { wrapper = IsWord(ref lexer, "CONDITIONAL") ? SqlJsonWrapper.Conditional : SqlJsonWrapper.Unconditional; Advance(ref lexer); }
                if (IsWord(ref lexer, "ARRAY")) { Advance(ref lexer); }
                ExpectWord(ref lexer, "WRAPPER");
            }
            if (IsWord(ref lexer, "KEEP") || IsWord(ref lexer, "OMIT"))
            {
                omit = IsWord(ref lexer, "OMIT"); Advance(ref lexer); ExpectWord(ref lexer, "QUOTES");
                if (IsWord(ref lexer, "ON"))
                { Advance(ref lexer); ExpectWord(ref lexer, "SCALAR"); ExpectWord(ref lexer, "STRING"); }
            }
        }
        var empty = SqlJsonBehavior.Null;
        var error = kind == SqlJsonFunctionKind.Exists ? SqlJsonBehavior.False : SqlJsonBehavior.Null;
        SqlExpression? emptyDefault = null, errorDefault = null;
        bool seenEmpty = false, seenError = false;
        while (IsJsonBehavior(ref lexer))
        {
            var behavior = ParseJsonBehavior(ref lexer, out var expression);
            ExpectWord(ref lexer, "ON");
            if (IsWord(ref lexer, "EMPTY") && kind != SqlJsonFunctionKind.Exists && !seenEmpty && !seenError)
            { empty = behavior; emptyDefault = expression; seenEmpty = true; Advance(ref lexer); }
            else if (IsWord(ref lexer, "ERROR") && !seenError)
            { error = behavior; errorDefault = expression; seenError = true; Advance(ref lexer); }
            else { AddSyntaxDiagnostic(ref lexer, ErrorMessages.JsonBehaviorInvalid); break; }
            bool valid = kind == SqlJsonFunctionKind.Exists ? behavior is SqlJsonBehavior.True or SqlJsonBehavior.False or SqlJsonBehavior.Unknown or SqlJsonBehavior.Error
                : behavior is SqlJsonBehavior.Null or SqlJsonBehavior.Error or SqlJsonBehavior.Default
                    || kind == SqlJsonFunctionKind.Query && behavior is SqlJsonBehavior.EmptyArray or SqlJsonBehavior.EmptyObject;
            if (!valid) { AddSyntaxDiagnostic(ref lexer, ErrorMessages.JsonBehaviorInvalid); }
        }
        if (omit && wrapper != SqlJsonWrapper.Without) { AddSyntaxDiagnostic(ref lexer, ErrorMessages.JsonBehaviorInvalid); }
        Expect(ref lexer, TokenType.RightParen, ")");
        return Nest(ref lexer, new SqlJsonFunctionExpression(kind, context, path, returning, returningType,
            wrapper, omit, empty, emptyDefault, error, errorDefault, Location.Create(1, 1, start, _lastTokenEnd)));
    }

    private static bool IsJsonBehavior(ref TokenLexer lexer)
        => IsWord(ref lexer, "ERROR") || IsWord(ref lexer, "NULL") || IsWord(ref lexer, "DEFAULT")
            || IsWord(ref lexer, "EMPTY") || IsWord(ref lexer, "TRUE") || IsWord(ref lexer, "FALSE") || IsWord(ref lexer, "UNKNOWN");

    private SqlJsonBehavior ParseJsonBehavior(ref TokenLexer lexer, out SqlExpression? expression)
    {
        expression = null;
        if (IsWord(ref lexer, "DEFAULT"))
        { Advance(ref lexer); expression = ParseOperand(ref lexer, OperandRule.Expression); return SqlJsonBehavior.Default; }
        if (IsWord(ref lexer, "EMPTY"))
        {
            Advance(ref lexer);
            if (IsWord(ref lexer, "OBJECT")) { Advance(ref lexer); return SqlJsonBehavior.EmptyObject; }
            if (IsWord(ref lexer, "ARRAY")) { Advance(ref lexer); }
            return SqlJsonBehavior.EmptyArray;
        }
        var result = IsWord(ref lexer, "ERROR") ? SqlJsonBehavior.Error : IsWord(ref lexer, "TRUE") ? SqlJsonBehavior.True
            : IsWord(ref lexer, "FALSE") ? SqlJsonBehavior.False : IsWord(ref lexer, "UNKNOWN") ? SqlJsonBehavior.Unknown : SqlJsonBehavior.Null;
        Advance(ref lexer);
        return result;
    }
}

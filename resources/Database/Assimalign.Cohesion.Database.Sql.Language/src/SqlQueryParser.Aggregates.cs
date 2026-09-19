using System;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

public sealed partial class SqlQueryParser
{
    /// <summary>Rejects empty grouping sets and ordinal syntax outside ORDER BY.</summary>
    private SqlExpression ParseGroupingExpression(ref TokenLexer lexer)
    {
        if (lexer.Current.Type == TokenType.LeftParen && TryPeekToken(lexer, out string next, out int end) && next == ")")
        {
            int start = lexer.Current.Position;
            AddUnsupportedSurfaceDiagnostic(start, end,
                "The empty SQL grouping set GROUP BY () is not supported; use an ungrouped aggregate.");
            Advance(ref lexer);
            Advance(ref lexer);
            return new SqlLiteralExpression("NULL", SqlLiteralType.Null, Location.Create(1, 1, start, end));
        }

        var expression = ParseExpression(ref lexer);
        var numeric = expression switch
        {
            SqlLiteralExpression { LiteralType: SqlLiteralType.Integer or SqlLiteralType.Float } literal => literal,
            SqlUnaryExpression
            {
                Operator: SqlUnaryOperator.Negate,
                Operand: SqlLiteralExpression { LiteralType: SqlLiteralType.Integer or SqlLiteralType.Float } literal,
            } => literal,
            _ => null,
        };
        if (numeric is not null)
        {
            AddUnsupportedSurfaceDiagnostic(expression.Location?.Start ?? 0, numeric.Location?.End ?? 0,
                "SQL select-list ordinals in GROUP BY are not supported; use the source grouping expression. Ordinals are supported only in ORDER BY.");
        }
        return expression;
    }

    /// <summary>
    /// Identifies the aggregate names implemented by the SQL grouping executor.
    /// </summary>
    private static bool IsAggregateFunction(string name) => name.ToUpperInvariant() is
        "COUNT" or "SUM" or "AVG" or "MIN" or "MAX";

    /// <summary>
    /// Recognizes aggregate extensions without reserving their names as column identifiers.
    /// </summary>
    private static bool TryGetUnsupportedAggregateClause(TokenLexer lexer, out string clause, out int end)
    {
        clause = string.Empty;
        end = lexer.Current.Position + lexer.Current.Value.Length;
        if (lexer.Current.Type is not (TokenType.Identifier or TokenType.Keyword or TokenType.Function) ||
            !TryPeekToken(lexer, out string next, out int nextEnd))
        {
            return false;
        }

        string token = CurrentText(ref lexer).ToUpperInvariant();
        if (token == "GROUPING" && next.Equals("SETS", StringComparison.OrdinalIgnoreCase) &&
            HasGroupingSetsArguments(lexer))
        {
            clause = "GROUPING SETS";
            end = nextEnd;
        }
        else if (token == "WITHIN" && next.Equals("GROUP", StringComparison.OrdinalIgnoreCase))
        {
            clause = "WITHIN GROUP";
            end = nextEnd;
        }
        else if (next == "(" && token is "ROLLUP" or "CUBE" or "GROUPING" or "GROUPING_ID" or "FILTER")
        {
            clause = token;
        }

        return clause.Length > 0;
    }

    /// <summary>Distinguishes GROUPING SETS (...) from a column and its implicit alias.</summary>
    private static bool HasGroupingSetsArguments(TokenLexer lexer) =>
        AdvancePastComments(ref lexer) && AdvancePastComments(ref lexer) &&
        lexer.Current.Type == TokenType.LeftParen;

    /// <summary>
    /// Recognizes calls to window functions even when their required OVER clause is absent.
    /// </summary>
    private static bool IsWindowFunctionCall(TokenLexer lexer) =>
        lexer.Current.Type == TokenType.Function &&
        CurrentText(ref lexer).ToUpperInvariant() is
            "ROW_NUMBER" or "RANK" or "DENSE_RANK" or "LEAD" or "LAG" or
            "FIRST_VALUE" or "LAST_VALUE" or "NTH_VALUE" or "NTILE" &&
        TryPeekToken(lexer, out string next, out _) && next == "(";
}

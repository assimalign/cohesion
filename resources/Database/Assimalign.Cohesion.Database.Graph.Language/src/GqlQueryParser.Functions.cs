using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private GqlExpression ParseNamedFunction()
    {
        var start = Current;
        if (!HasStackToNest(start)) { return new GqlLiteralExpression(null, Span(start, start)); }
        string name = Current.Text;
        Advance();
        Expect(TokenType.LeftParen, "'('");
        bool distinct = Take("DISTINCT");
        if (!distinct) { TakeSetQuantifierAll(); }
        var arguments = new System.Collections.Generic.List<GqlExpression>();
        if (Current.Type != TokenType.RightParen)
        {
            do { arguments.Add(ParsePredicate()); } while (Take(TokenType.Comma));
        }
        Expect(TokenType.RightParen, "')'");
        return new GqlNamedFunctionCallExpression(name, arguments, distinct, Span(start, Previous));
    }

    private GqlExpression ParseFunction()
    {
        var start = Current;
        if (!HasStackToNest(start)) { return new GqlLiteralExpression(null, Span(start, start)); }
        var signature = GqlScalarFunctions.Signatures[start.Text];
        Advance();
        Expect(TokenType.LeftParen, "'('");
        List<GqlExpression> arguments = [];
        if (Current.Type != TokenType.RightParen)
        {
            if (Is("DISTINCT") || Is("ALL") && !StartsListPredicate())
            { Error("CG00002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GqlQueryParserParseFunctionVoid, start.Text), Current); }
            if (!Failed)
            { do { arguments.Add(ParseExpression(0)); } while (!Failed && Take(TokenType.Comma)); }
        }
        Expect(TokenType.RightParen, "')'");
        if (!Failed && (arguments.Count < signature.Min || arguments.Count > signature.Max))
        { Error("CG00002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GqlQueryParserParseFunctionVoid2, start.Text, signature.Min, signature.Max), start); }
        return new GqlFunctionCallExpression(signature.Function, arguments, Span(start, Previous));
    }

    private GqlExpression ParseCast()
    {
        var start = Current;
        if (!HasStackToNest(start)) { return new GqlLiteralExpression(null, Span(start, start)); }
        Advance();
        Expect(TokenType.LeftParen, "'('");
        var operand = ParseExpression(0);
        Expect("AS");
        if (Failed) { return new GqlCastExpression(operand, default, Span(start, Previous)); }
        var type = Current;
        string target = Identifier(allowKeyword: true).ToUpperInvariant();
        if (Failed) { return new GqlCastExpression(operand, default, Span(start, Previous)); }
        GqlCastType kind = target switch
        {
            "INT" or "INTEGER" => GqlCastType.Int32,
            "INT64" or "BIGINT" => GqlCastType.Int64,
            "REAL" => GqlCastType.Float32,
            "FLOAT" or "DOUBLE" or "FLOAT64" => GqlCastType.Float64,
            "DECIMAL" or "NUMERIC" => GqlCastType.Decimal,
            "STRING" or "CHAR" or "VARCHAR" => GqlCastType.String,
            "BOOL" or "BOOLEAN" => GqlCastType.Boolean,
            "DATE" => GqlCastType.Date,
            _ => GqlCastType.Timestamp,
        };
        switch (target)
        {
            case "ZONED":
                if (Take("TIME")) { Unsupported("CAST ZONED TIME", type); }
                else { Expect("DATETIME"); }
                break;
            case "TIMESTAMP":
                if (Take("WITHOUT"))
                {
                    Expect("TIME"); Expect("ZONE");
                    if (!Failed) { Unsupported("CAST TIMESTAMP WITHOUT TIME ZONE", type); }
                }
                else { Expect("WITH"); Expect("TIME"); Expect("ZONE"); }
                break;
            case "DOUBLE": Take("PRECISION"); break;
            case "LOCAL":
                if (Take("DATETIME")) { Unsupported("CAST LOCAL DATETIME", type); }
                else { Unsupported("CAST LOCAL", type); }
                break;
            case "INT32": case "SMALLINT": case "FLOAT32":
            case "LIST": case "DURATION": case "BYTES": case "TIME":
                Unsupported("CAST " + target, type); break;
            default:
                if (kind == GqlCastType.Timestamp) { Error("CG00002", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.GqlQueryParserParseCastVoid, target), type); }
                break;
        }
        Expect(TokenType.RightParen, "')'");
        return new GqlCastExpression(operand, kind, Span(start, Previous));
    }
}

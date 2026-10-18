using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
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
            if (Is("DISTINCT") || Is("ALL"))
            { Error("GQL0002", $"Function {start.Text} does not accept a set quantifier.", Current); }
            if (!Failed)
            { do { arguments.Add(ParseExpression(0)); } while (!Failed && Take(TokenType.Comma)); }
        }
        Expect(TokenType.RightParen, "')'");
        if (!Failed && (arguments.Count < signature.Min || arguments.Count > signature.Max))
        { Error("GQL0002", $"Function {start.Text} requires {signature.Min} to {signature.Max} arguments.", start); }
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
            case "TIMESTAMP": Expect("WITH"); Expect("TIME"); Expect("ZONE"); break;
            case "DOUBLE": Take("PRECISION"); break;
            case "LIST": case "DURATION": case "BYTES": case "TIME": case "LOCAL":
                Unsupported("CAST " + target, type); break;
            default:
                if (kind == GqlCastType.Timestamp) { Error("GQL0002", $"Unknown CAST target '{target}'.", type); }
                break;
        }
        Expect(TokenType.RightParen, "')'");
        return new GqlCastExpression(operand, kind, Span(start, Previous));
    }
}

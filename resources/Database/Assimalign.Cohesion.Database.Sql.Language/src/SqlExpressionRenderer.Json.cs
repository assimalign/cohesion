namespace Assimalign.Cohesion.Database.Sql.Language;

public static partial class SqlExpressionRenderer
{
    private static string JsonFunction(SqlJsonFunctionExpression json)
    {
        string text = "JSON_" + json.Kind.ToString().ToUpperInvariant() + "(" + Expression(json.Context) + ", " + Expression(json.Path);
        if (json.Returning is not null)
        { text += " RETURNING " + (json.ReturningType is { } type ? CastTarget(json.Returning) : Identifier(json.Returning)); }
        if (json.Kind == SqlJsonFunctionKind.Query)
        {
            text += json.Wrapper switch
            {
                SqlJsonWrapper.Conditional => " WITH CONDITIONAL ARRAY WRAPPER",
                SqlJsonWrapper.Unconditional => " WITH UNCONDITIONAL ARRAY WRAPPER",
                _ => " WITHOUT ARRAY WRAPPER",
            };
            text += json.OmitQuotes ? " OMIT QUOTES" : " KEEP QUOTES";
        }
        if (json.Kind != SqlJsonFunctionKind.Exists) { text += " " + Behavior(json.OnEmpty, json.EmptyDefault) + " ON EMPTY"; }
        return text + " " + Behavior(json.OnError, json.ErrorDefault) + " ON ERROR)";

        static string Behavior(SqlJsonBehavior behavior, SqlExpression? expression) => behavior switch
        {
            SqlJsonBehavior.Default => "DEFAULT " + Expression(expression!),
            SqlJsonBehavior.EmptyArray => "EMPTY ARRAY",
            SqlJsonBehavior.EmptyObject => "EMPTY OBJECT",
            _ => behavior.ToString().ToUpperInvariant(),
        };
    }
}

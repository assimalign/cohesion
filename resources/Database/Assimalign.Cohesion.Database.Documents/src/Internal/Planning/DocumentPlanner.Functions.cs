using System.Text.Json;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    // Available before data access and paging, including for an empty grouped result (#1129).
    internal static DatabaseType StaticType(OqlExpression expression, DocumentFunctionCatalog? functions = null) => expression switch
    {
        OqlLiteralExpression { Value: JsonElement json } => json.ValueKind switch
        {
            JsonValueKind.String => DatabaseType.String,
            JsonValueKind.Number => DatabaseType.Decimal,
            JsonValueKind.True or JsonValueKind.False => DatabaseType.Boolean,
            JsonValueKind.Array or JsonValueKind.Object => DatabaseType.Json,
            _ => DatabaseType.Null,
        },
        OqlLiteralExpression { Value: string } => DatabaseType.String,
        OqlLiteralExpression { Value: bool } => DatabaseType.Boolean,
        OqlLiteralExpression { Value: decimal or int or long } => DatabaseType.Decimal,
        OqlArrayExpression or OqlObjectExpression or OqlStarExpression => DatabaseType.Json,
        OqlComprehensionExpression comprehension => comprehension.IsFirst ? StaticType(comprehension.Projection, functions) : DatabaseType.Json,
        OqlMetaExpression meta => meta.Member == "id" ? DatabaseType.String : DatabaseType.Decimal,
        OqlUnaryExpression unary when unary.Operator is "+" or "-" => DatabaseType.Decimal,
        OqlUnaryExpression => DatabaseType.Boolean,
        OqlBinaryExpression binary => binary.Operator is "+" or "-" or "*" or "/" or "%" ? DatabaseType.Decimal : DatabaseType.Boolean,
        OqlCallExpression call => (functions ?? DocumentFunctionCatalog.Standard).Find(call.Name, call.Arguments.Count == 1 && call.Arguments[0] is OqlStarExpression ? 0 : call.Arguments.Count)?.ReturnType ?? OqlFunctions.Find(call.Name)?.ResultType ?? DatabaseType.Null,
        OqlQuantifiedExpression or OqlInExpression or OqlBetweenExpression or OqlLikeExpression => DatabaseType.Boolean,
        _ => DatabaseType.Null,
    };

    private static void ValidateFunctionKind(DocumentFunction function, int index, OqlExpression argument)
    {
        // Only a literal (including unary minus) or constructor has a statically wrong kind.
        // Calls and paths may return NULL/MISSING at runtime, so their metadata is not a proof.
        var literal = argument;
        while (literal is OqlUnaryExpression { Operator: "+" or "-" } unary) { literal = unary.Operand; }
        if (literal is not (OqlLiteralExpression or OqlArrayExpression or OqlObjectExpression)) { return; }
        var kind = literal switch
        {
            OqlArrayExpression => JsonValueKind.Array,
            OqlObjectExpression => JsonValueKind.Object,
            OqlLiteralExpression value => DocumentExpressionEvaluator.FunctionKind(value.Value),
            _ => JsonValueKind.Undefined,
        };
        if (kind is not (JsonValueKind.Null or JsonValueKind.Undefined) && !function.AcceptsArgument(index, kind))
        {
            throw new DocumentPlanningException("CD05001", ErrorMessages.FunctionArgumentKindInvalid, argument.Location);
        }
    }
}

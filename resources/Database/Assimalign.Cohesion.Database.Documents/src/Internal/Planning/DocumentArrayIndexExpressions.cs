using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// The language assembly owns syntax; only the engine binds it to catalog-owned values.
internal static class DocumentArrayIndexExpressions
{
    internal static DocumentIndexKey BindKey(OqlExpression expression)
    {
        if (expression is OqlPathExpression scalar)
        { return DocumentIndexKey.Scalar(Path(scalar)); }
        if (expression is not OqlComprehensionExpression { IsFirst: false, Collection: OqlPathExpression source } array)
        { throw new DocumentPlanningException("CD03027", ErrorMessages.ArrayIndexExpressionInvalid, expression.Location); }
        if (string.IsNullOrEmpty(array.Variable))
        { throw new DocumentPlanningException("CD01028", ErrorMessages.ArrayIndexBindingInvalid, array.Location); }
        string projection = RelativePath(array.Projection, array.Variable);
        DocumentIndexPredicate? when = null;
        if (array.Predicate is not null) { when = BindPredicate(array.Predicate, array.Variable); }
        return DocumentIndexKey.DistinctArray(Path(source), projection, when);
    }

    private static string Path(OqlPathExpression path)
    {
        if (path.Segments.Count == 0 || path.Segments.Any(segment =>
            (segment.Name is null) == (segment.Index is null) || segment.Index < 0))
        { throw new DocumentPlanningException("CD03027", ErrorMessages.ArrayIndexExpressionInvalid, path.Location); }
        return DocumentPlanner.IndexPath(path, null)!;
    }

    internal static string RelativePath(OqlExpression expression, string variable)
    {
        if (expression is not OqlPathExpression path)
        { throw new DocumentPlanningException("CD03027", ErrorMessages.ArrayIndexExpressionInvalid, expression.Location); }
        Path(path);
        if (path.Segments[0].Name != variable)
        { throw new DocumentPlanningException("CD01028", ErrorMessages.ArrayIndexBindingInvalid, path.Location); }
        return DocumentPlanner.IndexPath(path, variable) ?? string.Empty;
    }

    internal static DocumentIndexPredicate BindPredicate(OqlExpression expression, string variable)
    {
        int nodes = 0;
        return Bind(expression, 1);

        DocumentIndexPredicate Bind(OqlExpression node, int depth)
        {
            if (++nodes > 128 || depth > 32)
            { throw new DocumentPlanningException("CD03028", ErrorMessages.ArrayIndexPredicateTooLarge, node.Location); }
            if (node is OqlBinaryExpression { Operator: "AND" or "OR" } logical)
            {
                var left = Bind(logical.Left, depth + 1);
                var right = Bind(logical.Right, depth + 1);
                return logical.Operator == "AND" ? DocumentIndexPredicate.And(left, right) : DocumentIndexPredicate.Or(left, right);
            }
            if (node is OqlUnaryExpression unary)
            {
                if (unary.Operator == "NOT") { return DocumentIndexPredicate.Not(Bind(unary.Operand, depth + 1)); }
                bool negated = unary.Operator is "IS NOT NULL" or "IS NOT MISSING";
                if (unary.Operator is "IS NULL" or "IS NOT NULL" or "IS MISSING" or "IS NOT MISSING")
                {
                    // Negated tests persist as NOT(test), and consume both nodes/levels.
                    if (negated && (++nodes > 128 || depth + 1 > 32))
                    { throw new DocumentPlanningException("CD03028", ErrorMessages.ArrayIndexPredicateTooLarge, node.Location); }
                    string path = RelativePath(unary.Operand, variable);
                    var test = unary.Operator.EndsWith("NULL", StringComparison.Ordinal)
                        ? DocumentIndexPredicate.IsNull(path) : DocumentIndexPredicate.IsMissing(path);
                    return negated ? DocumentIndexPredicate.Not(test) : test;
                }
            }
            if (node is OqlBinaryExpression comparison && Comparison(comparison.Operator) is { } operation)
            {
                OqlExpression operand = comparison.Left, constant = comparison.Right;
                if (operand is not OqlPathExpression && constant is OqlPathExpression)
                { (operand, constant) = (constant, operand); operation = Reverse(operation); }
                return DocumentIndexPredicate.Compare(RelativePath(operand, variable), operation, Constant(constant));
            }
            throw new DocumentPlanningException("CD03027", ErrorMessages.ArrayIndexExpressionInvalid, node.Location);
        }
    }

    private static object? Constant(OqlExpression expression)
    {
        if (expression is OqlUnaryExpression { Operator: "+" or "-", Operand: OqlLiteralExpression } unary)
        {
            if (Constant(unary.Operand) is decimal number) { return unary.Operator == "-" ? -number : number; }
        }
        if (expression is OqlLiteralExpression literal)
        {
            if (literal.Value is null or bool or decimal or string) { return literal.Value; }
            if (literal.Value is byte or sbyte or short or ushort or int or uint or long or ulong)
            { return Convert.ToDecimal(literal.Value, CultureInfo.InvariantCulture); }
            if (literal.Value is JsonElement { ValueKind: JsonValueKind.Number or JsonValueKind.String or
                JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null } json)
            {
                try { return DocumentExpressionEvaluator.Normalize(json); }
                catch (DocumentPlanningException error)
                { throw new DocumentPlanningException("CD02027", ErrorMessages.ArrayIndexConstantInvalid, expression.Location, error); }
            }
        }
        throw new DocumentPlanningException("CD02027", ErrorMessages.ArrayIndexConstantInvalid, expression.Location);
    }

    private static DocumentIndexComparison? Comparison(string operation) => operation switch
    {
        "=" => DocumentIndexComparison.Equal, "!=" or "<>" => DocumentIndexComparison.NotEqual,
        "<" => DocumentIndexComparison.LessThan, "<=" => DocumentIndexComparison.LessThanOrEqual,
        ">" => DocumentIndexComparison.GreaterThan, ">=" => DocumentIndexComparison.GreaterThanOrEqual, _ => null,
    };

    private static DocumentIndexComparison Reverse(DocumentIndexComparison operation) => operation switch
    {
        DocumentIndexComparison.LessThan => DocumentIndexComparison.GreaterThan,
        DocumentIndexComparison.LessThanOrEqual => DocumentIndexComparison.GreaterThanOrEqual,
        DocumentIndexComparison.GreaterThan => DocumentIndexComparison.LessThan,
        DocumentIndexComparison.GreaterThanOrEqual => DocumentIndexComparison.LessThanOrEqual, _ => operation,
    };
}

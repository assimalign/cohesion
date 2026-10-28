using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Assimalign.Cohesion.Database.Graph.Language.Internal;

// Like label rendering, path and scalar rendering use explicit work stacks. Source spans
// and original spellings are not syntax: all semantic children are rendered from the AST.
internal static class GqlPathRenderer
{
    internal static string Render(GqlPathPattern path)
    {
        var builder = new StringBuilder();
        var pending = new Stack<object>();
        pending.Push(path);
        while (pending.TryPop(out var item))
        {
            switch (item)
            {
                case string text: builder.Append(text); break;
                case GqlPathPattern pattern:
                    if (pattern.Variable is { } variable) { builder.Append(Name(variable)).Append(" = "); }
                    if (pattern.Selector is { } selector)
                    {
                        // GROUPS belongs after the optional mode and PATH token.
                        builder.Append(selector.Kind == GqlPathSelectorKind.ShortestGroups
                            ? "SHORTEST " + selector.Count.ToString(CultureInfo.InvariantCulture) : selector.ToString());
                        builder.Append(' ');
                    }
                    if (pattern.Mode is { } mode) { builder.Append(mode.ToString().ToUpperInvariant()).Append(' '); }
                    if (pattern.Selector is not null || pattern.Mode is not null) { builder.Append("PATH "); }
                    if (pattern.Selector?.Kind == GqlPathSelectorKind.ShortestGroups) { builder.Append("GROUPS "); }
                    if (pattern.Predicate is null) { Push(new PathBody(pattern)); }
                    else { Push("(", new PathBody(pattern), ")"); }
                    break;
                case PathBody body:
                    if (body.Pattern.Predicate is { } predicate) { Push(" WHERE ", predicate); }
                    if (body.Pattern.Elements is { } elements)
                    { for (int i = elements.Count - 1; i >= 0; i--) { pending.Push(elements[i]); } }
                    else
                    {
                        for (int i = body.Pattern.Nodes.Count - 1; i >= 0; i--)
                        {
                            if (i < body.Pattern.Relationships.Count) { pending.Push(body.Pattern.Relationships[i]); }
                            pending.Push(body.Pattern.Nodes[i]);
                        }
                    }
                    break;
                case GqlPathNode node: pending.Push(node.Pattern); break;
                case GqlPathRelationship edge: pending.Push(edge.Pattern); break;
                case GqlParenthesizedPathPattern group:
                    Push(" (", new PathBody(group.Pattern), ")", group.Quantifier, " ");
                    break;
                case GqlNodePattern node:
                    Push("(", node.Variable is null ? null : Name(node.Variable), Labels(node.LabelExpression, node.Labels),
                        new PropertyMap(node.Properties), node.Predicate is null ? null : " WHERE ", node.Predicate, ")");
                    break;
                case GqlRelationshipPattern edge:
                    string left = edge.Direction is GqlPatternDirection.Incoming or GqlPatternDirection.LeftOrRight ? "<-[" : "-[";
                    string right = edge.Direction is GqlPatternDirection.Outgoing or GqlPatternDirection.LeftOrRight ? "]->" : "]-";
                    // ISO excludes {0}; the admitted Cypher zero-edge spelling is inside the filler.
                    bool zero = edge.Quantifier is { Max: 0 };
                    Push(left, edge.Variable is null ? null : Name(edge.Variable),
                        Labels(edge.LabelExpression, edge.Type is null ? [] : [edge.Type]), zero ? "*0" : null,
                        new PropertyMap(edge.Properties), edge.Predicate is null ? null : " WHERE ", edge.Predicate,
                        right, zero ? null : edge.Quantifier);
                    break;
                case GqlQuantifier quantifier:
                    builder.Append('{').Append(quantifier.Min.ToString(CultureInfo.InvariantCulture));
                    if (quantifier.Max != quantifier.Min)
                    { builder.Append(','); if (quantifier.Max is { } max) { builder.Append(max.ToString(CultureInfo.InvariantCulture)); } }
                    builder.Append('}');
                    break;
                case PropertyMap map:
                    if (map.Properties.Count != 0) { Push(" ", new MapBody(map.Properties)); }
                    break;
                case MapBody map:
                    Push("}");
                    var entries = map.Properties.ToArray();
                    for (int i = entries.Length - 1; i >= 0; i--)
                    { Push(i == 0 ? null : ", ", Name(entries[i].Key), ": ", entries[i].Value); }
                    Push("{");
                    break;
                case GqlExpression expression: Expression(expression); break;
                default: builder.Append("<invalid>"); break;
            }
        }
        return builder.ToString();

        void Push(params object?[] steps)
        { for (int i = steps.Length - 1; i >= 0; i--) { if (steps[i] is { } step) { pending.Push(step); } } }

        void Arguments(IReadOnlyList<GqlExpression> arguments, string name, bool distinct = false)
        {
            Push(")");
            for (int i = arguments.Count - 1; i >= 0; i--) { Push(i == 0 ? null : ", ", arguments[i]); }
            Push(name, "(", distinct ? "DISTINCT " : null);
        }

        void Expression(GqlExpression expression)
        {
            switch (expression)
            {
                case GqlLiteralExpression literal:
                    builder.Append(literal.Value switch
                    {
                        null => "NULL", bool value => value ? "TRUE" : "FALSE",
                        string value => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'",
                        double value => Floating(value),
                        IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
                        _ => "<invalid>",
                    });
                    break;
                case GqlVariableExpression variable: builder.Append(Name(variable.Variable)); break;
                case GqlParameterExpression parameter: builder.Append('$').Append(parameter.Name); break;
                case GqlPropertyExpression property: Push(Name(property.Variable), ".", Name(property.Property)); break;
                case GqlMemberExpression member: Push("(", member.Operand, ").", Name(member.Member)); break;
                case GqlSubscriptExpression subscript: Push("(", subscript.Operand, ")[", subscript.Index, "]"); break;
                case GqlListSliceExpression slice: Push("(", slice.Operand, ")[", slice.From, "..", slice.To, "]"); break;
                case GqlListComprehensionExpression comprehension:
                    Push("[", Name(comprehension.Variable), " IN ", comprehension.List,
                        comprehension.Predicate is null ? null : " WHERE ", comprehension.Predicate,
                        comprehension.Projection is null ? null : " | ", comprehension.Projection, "]");
                    break;
                case GqlBinaryExpression binary: Push("(", binary.Left, " " + binary.Operator + " ", binary.Right, ")"); break;
                case GqlLogicalExpression logical:
                    Push(")");
                    for (int i = logical.Operands.Count - 1; i >= 0; i--)
                    { Push(i == 0 ? null : " " + logical.Operator.ToString().ToUpperInvariant() + " ", logical.Operands[i]); }
                    Push("(");
                    break;
                case GqlUnaryExpression unary: Push("(", unary.Operator, " ", unary.Operand, ")"); break;
                case GqlIsNullExpression test: Push("(", test.Operand, test.IsNegated ? " IS NOT NULL)" : " IS NULL)"); break;
                case GqlTruthTestExpression test:
                    Push("(", test.Operand, test.IsNegated ? " IS NOT " : " IS ", test.TruthValue is null ? "UNKNOWN" : test.TruthValue.Value ? "TRUE" : "FALSE", ")");
                    break;
                case GqlInExpression membership: Push("(", membership.Operand, " IN ", membership.List, ")"); break;
                case GqlStringPredicateExpression test:
                    Push("(", test.Left, test.Operator switch { GqlStringPredicateOperator.StartsWith => " STARTS WITH ", GqlStringPredicateOperator.EndsWith => " ENDS WITH ", _ => " CONTAINS " }, test.Right, ")");
                    break;
                case GqlLabeledPredicate labeled:
                    Push("(", Name(labeled.Variable), labeled.IsNegated ? " IS NOT LABELED " : " IS LABELED ", labeled.LabelExpression.ToString(), ")");
                    break;
                case GqlListExpression list:
                    Push("]");
                    for (int i = list.Items.Count - 1; i >= 0; i--) { Push(i == 0 ? null : ", ", list.Items[i]); }
                    Push("[");
                    break;
                case GqlMapExpression map: Push(new MapBody(map.Entries)); break;
                case GqlFunctionCallExpression call:
                    string name = GqlScalarFunctions.Signatures.First(pair => pair.Value.Function == call.Function).Key;
                    if (call.Function == GqlScalarFunction.Log && call.Arguments.Count == 2) { name = "log"; }
                    if (call.IsNiladic) { builder.Append(name.ToUpperInvariant()); }
                    else { Arguments(call.Arguments, name); }
                    break;
                case GqlNamedFunctionCallExpression call: Arguments(call.Arguments, call.Name.ToString(), call.Distinct); break;
                case GqlAggregateExpression aggregate:
                    if (aggregate.Operand is null) { Push("COUNT(*)"); }
                    else { Arguments([aggregate.Operand], aggregate.Function == GqlAggregateFunction.CollectList ? "COLLECT_LIST" : aggregate.Function.ToString().ToUpperInvariant(), aggregate.Distinct); }
                    break;
                case GqlCastExpression cast:
                    Push("CAST(", cast.Operand, " AS ", cast.Target switch
                    {
                        GqlCastType.Int32 => "INT", GqlCastType.Int64 => "BIGINT", GqlCastType.Float32 => "REAL",
                        GqlCastType.Float64 => "DOUBLE PRECISION", GqlCastType.Decimal => "DECIMAL", GqlCastType.String => "STRING",
                        GqlCastType.Boolean => "BOOLEAN", GqlCastType.Date => "DATE", _ => "ZONED DATETIME",
                    }, ")");
                    break;
                case GqlCoalesceExpression coalesce: Arguments(coalesce.Operands, "COALESCE"); break;
                case GqlNullIfExpression nullIf: Arguments([nullIf.Left, nullIf.Right], "NULLIF"); break;
                case GqlCaseExpression conditional:
                    Push(" END");
                    if (conditional.Otherwise is { } otherwise) { Push(" ELSE ", otherwise); }
                    for (int i = conditional.Alternatives.Count - 1; i >= 0; i--)
                    {
                        var alternative = conditional.Alternatives[i];
                        string comparison = conditional.Operand is null || alternative.Comparison == "=" ? "" : alternative.Comparison + " ";
                        Push(" WHEN ", comparison, comparison.StartsWith("IS", StringComparison.Ordinal) ? null : alternative.When, " THEN ", alternative.Then);
                    }
                    Push("CASE ", conditional.Operand);
                    break;
                default: builder.Append("<invalid>"); break;
            }
        }
    }

    private static string Name(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string Labels(GqlLabelExpression? expression, IReadOnlyList<string> names)
        => expression is not null ? ":" + expression : names.Count == 0 ? "" : ":" + string.Join("&", names.Select(Name));
    private static string Floating(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    private readonly record struct PathBody(GqlPathPattern Pattern);
    private readonly record struct PropertyMap(IReadOnlyDictionary<string, GqlExpression> Properties);
    private readonly record struct MapBody(IEnumerable<KeyValuePair<string, GqlExpression>> Properties);
}

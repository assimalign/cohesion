using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    // Keep window-only nested delegates off CreateLogicalPlan's pinned closure layout.
    private static bool ContainsWindowKeyAggregate(OqlSelectExpression query, DocumentFunctionCatalog functions)
    {
        foreach (var definition in query.Windows)
        {
            foreach (var key in definition.Specification.PartitionBy) { if (ContainsAggregate(key, functions)) { return true; } }
            foreach (var order in definition.Specification.OrderBy) { if (ContainsAggregate(order.Expression, functions)) { return true; } }
        }
        return false;
    }

    internal static bool HasWindows(OqlSelectExpression query)
    {
        for (int i = 0; i < query.Projections.Count; i++) { if (ContainsWindow(query.Projections[i].Expression)) { return true; } }
        for (int i = 0; i < query.OrderBy.Count; i++) { if (ContainsWindow(query.OrderBy[i].Expression)) { return true; } }
        return false;
    }

    private static bool ContainsWindow(OqlExpression expression)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            case OqlWindowExpression: return true;
            case OqlCallExpression call:
                for (int i = 0; i < call.Arguments.Count; i++) { if (ContainsWindow(call.Arguments[i])) { return true; } }
                break;
            case OqlArrayExpression array:
                for (int i = 0; i < array.Elements.Count; i++) { if (ContainsWindow(array.Elements[i])) { return true; } }
                break;
            case OqlObjectExpression obj:
                for (int i = 0; i < obj.Members.Count; i++) { if (ContainsWindow(obj.Members[i].Value)) { return true; } }
                break;
            case OqlBinaryExpression binary: return ContainsWindow(binary.Left) || ContainsWindow(binary.Right);
            case OqlUnaryExpression unary: return ContainsWindow(unary.Operand);
            case OqlComprehensionExpression c: return ContainsWindow(c.Collection) || ContainsWindow(c.Projection) || c.Predicate is not null && ContainsWindow(c.Predicate);
            case OqlQuantifiedExpression q: return ContainsWindow(q.Collection) || ContainsWindow(q.Predicate);
            case OqlInExpression m:
                if (ContainsWindow(m.Operand) || m.Array is not null && ContainsWindow(m.Array)) { return true; }
                for (int i = 0; i < m.Values.Count; i++) { if (ContainsWindow(m.Values[i])) { return true; } }
                break;
            case OqlBetweenExpression b: return ContainsWindow(b.Operand) || ContainsWindow(b.Lower) || ContainsWindow(b.Upper);
            case OqlLikeExpression l: return ContainsWindow(l.Operand) || ContainsWindow(l.Pattern) || l.Escape is not null && ContainsWindow(l.Escape);
            case OqlCaseExpression c:
                if (c.Operand is not null && ContainsWindow(c.Operand) || c.Otherwise is not null && ContainsWindow(c.Otherwise)) { return true; }
                for (int i = 0; i < c.Branches.Count; i++) { if (ContainsWindow(c.Branches[i].Key) || ContainsWindow(c.Branches[i].Value)) { return true; } }
                break;
        }
        return false;
    }

    internal static IEnumerable<OqlWindowExpression> Windows(OqlSelectExpression query)
    {
        foreach (var projection in query.Projections)
        { foreach (var window in Visit(projection.Expression)) { yield return window; } }
        foreach (var order in query.OrderBy)
        { foreach (var window in Visit(order.Expression)) { yield return window; } }

        static IEnumerable<OqlWindowExpression> Visit(OqlExpression expression)
        {
            if (expression is OqlWindowExpression window) { yield return window; yield break; }
            if (expression is OqlSubqueryExpression) { yield break; }
            foreach (var child in NestedChildren(expression))
            { foreach (var nested in Visit(child)) { yield return nested; } }
        }
    }

    internal static IEnumerable<OqlExpression> WindowChildren(OqlWindowExpression window)
    {
        foreach (var argument in window.Call.Arguments) { yield return argument; }
        if (window.Specification is { } specification)
        {
            foreach (var key in specification.PartitionBy) { yield return key; }
            foreach (var order in specification.OrderBy) { yield return order.Expression; }
        }
    }

    internal static Dictionary<string, OqlWindowSpecification> WindowDefinitions(OqlSelectExpression query)
    {
        var definitions = new Dictionary<string, OqlWindowSpecification>(StringComparer.Ordinal);
        foreach (var definition in query.Windows)
        {
            if (string.IsNullOrEmpty(definition.Name) || definitions.ContainsKey(definition.Name))
            { throw new DocumentPlanningException("CD01041", ErrorMessages.WindowNameDuplicate, definition.Location); }
            definitions.Add(definition.Name, ResolveWindow(definition.Specification, definitions));
        }
        return definitions;
    }

    internal static OqlWindowSpecification ResolveWindow(OqlWindowExpression window, IReadOnlyDictionary<string, OqlWindowSpecification> definitions)
    {
        if (window.Name is not null && window.Specification is null)
        {
            if (definitions.TryGetValue(window.Name, out var named)) { return named; }
            throw new DocumentPlanningException("CD01042", ErrorMessages.WindowReferenceUnknown, window.Location);
        }
        if (window.Name is not null || window.Specification is null)
        { throw new DocumentPlanningException("CD01043", ErrorMessages.WindowSpecificationInvalid, window.Location); }
        return ResolveWindow(window.Specification, definitions);
    }

    private static OqlWindowSpecification ResolveWindow(OqlWindowSpecification specification, IReadOnlyDictionary<string, OqlWindowSpecification> definitions)
    {
        if (specification.Reference is null) { return specification; }
        if (!definitions.TryGetValue(specification.Reference, out var source))
        { throw new DocumentPlanningException("CD01042", ErrorMessages.WindowReferenceUnknown, specification.Location); }
        if (specification.PartitionBy.Count > 0 || source.OrderBy.Count > 0 && specification.OrderBy.Count > 0)
        { throw new DocumentPlanningException("CD01043", ErrorMessages.WindowSpecificationInvalid, specification.Location); }
        return new OqlWindowSpecification(source.PartitionBy, specification.OrderBy.Count > 0 ? specification.OrderBy : source.OrderBy, location: specification.Location);
    }

    private static void ValidateWindows(OqlSelectExpression query, DocumentFunctionCatalog functions, IReadOnlySet<string>? outerNames)
    {
        var definitions = WindowDefinitions(query);
        foreach (var specification in definitions.Values) { ValidateKeys(specification); }
        foreach (var window in Windows(query))
        {
            ValidateWindowCall(window);
            ValidateKeys(ResolveWindow(window, definitions));
        }

        void ValidateKeys(OqlWindowSpecification specification)
        {
            foreach (var key in specification.PartitionBy) { Validate(key, true, functions: functions); ValidateQueryMetadata(key, query, outerNames); }
            foreach (var order in specification.OrderBy)
            {
                if (order.NullPlacement is not (OqlNullPlacement.Default or OqlNullPlacement.First or OqlNullPlacement.Last))
                { throw new DocumentPlanningException("CD01032", ErrorMessages.NullPlacementInvalid, order.Expression.Location); }
                Validate(order.Expression, true, functions: functions);
                ValidateQueryMetadata(order.Expression, query, outerNames);
            }
        }
    }

    private static void ValidateWindowCall(OqlWindowExpression window)
    {
        var function = OqlFunctions.Find(window.Call.Name);
        if (function is null || function.WindowKind == OqlWindowFunctionKind.None || window.Call.Quantifier != OqlAggregateQuantifier.None)
        { throw new DocumentPlanningException("CK00001", ErrorMessages.WindowFunctionDeferred, window.Location); }
        if (!function.AcceptsArity(window.Call.Arguments.Count))
        { throw new DocumentPlanningException("CD00006", ErrorMessages.FunctionArgumentsInvalid, window.Location); }
        if (function.WindowKind == OqlWindowFunctionKind.Ntile)
        { WindowInteger(window.Call.Arguments[0], positive: true); }
        if (function.WindowKind is OqlWindowFunctionKind.Lag or OqlWindowFunctionKind.Lead && window.Call.Arguments.Count > 1)
        { WindowInteger(window.Call.Arguments[1], positive: false); }
    }

    // W1 deliberately accepts integer literals only; unknown SQL++ argument rules are refused.
    internal static long WindowInteger(OqlExpression expression, bool positive)
    {
        var value = expression;
        if (value is OqlUnaryExpression { Operator: "+" } sign) { value = sign.Operand; }
        long? number = value is OqlLiteralExpression literal ? literal.Value switch
        {
            long n => n,
            int n => n,
            JsonElement { ValueKind: JsonValueKind.Number } json when json.TryGetInt64(out long n) => n,
            _ => null,
        } : null;
        if (number is long result && result >= (positive ? 1 : 0)) { return result; }
        throw new DocumentPlanningException("CK00001", ErrorMessages.WindowIntegerDeferred, expression.Location);
    }

    internal static bool EquivalentWindowSpecifications(OqlWindowSpecification left, OqlWindowSpecification right, OqlSelectExpression query)
    {
        var scopes = new DocumentBindingScopes();
        var variables = query.Bindings.Count == 0 ? null : query.Bindings.Select(b => b.Variable).ToHashSet(StringComparer.Ordinal);
        var groupScope = query.GroupAliases.Count > 0 || query.GroupAs is not null ? GroupScope(query, variables) : null;
        return left.PartitionBy.Count == right.PartitionBy.Count && left.OrderBy.Count == right.OrderBy.Count &&
            left.PartitionBy.Zip(right.PartitionBy).All(p => EquivalentExpression(p.First, p.Second, query, variables, scopes, groupScope, groupScope)) &&
            left.OrderBy.Zip(right.OrderBy).All(p => p.First.Descending == p.Second.Descending && p.First.NullPlacement == p.Second.NullPlacement &&
                EquivalentExpression(p.First.Expression, p.Second.Expression, query, variables, scopes, groupScope, groupScope));
    }

    private static bool EquivalentWindows(OqlWindowExpression left, OqlWindowExpression right, OqlSelectExpression query,
        IReadOnlySet<string>? variables, DocumentBindingScopes scopes, DocumentBindingScope? leftScope, DocumentBindingScope? rightScope, int localDepth)
    {
        var definitions = WindowDefinitions(query);
        return EquivalentExpression(left.Call, right.Call, query, variables, scopes, leftScope, rightScope, localDepth) &&
            EquivalentWindowSpecifications(ResolveWindow(left, definitions), ResolveWindow(right, definitions), query);
    }
}

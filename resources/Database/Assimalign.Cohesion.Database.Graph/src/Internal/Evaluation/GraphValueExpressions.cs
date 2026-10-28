using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphValueExpressions
{
    internal static bool ReferencesAny(GqlExpression root, IReadOnlySet<string> names, CancellationToken token)
    {
        var pending = new Stack<GqlExpression>();
        pending.Push(root);
        while (pending.TryPop(out var expression))
        {
            token.ThrowIfCancellationRequested();
            if (expression is GqlExistsExpression exists)
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                foreach (var input in ExistsInputs(exists))
                { if (ReferencesAny(input, names, token)) { return true; } }
                continue;
            }
            if (expression is GqlReduceExpression reduce)
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                if (ReferencesAny(reduce.List, names, token) || ReferencesAny(reduce.Initial, names, token)) { return true; }
                var inner = new HashSet<string>(names, StringComparer.Ordinal);
                inner.Remove(reduce.Variable); inner.Remove(reduce.Accumulator);
                if (ReferencesAny(reduce.Body, inner, token)) { return true; }
                continue;
            }
            if (expression is GqlListPredicateExpression predicate)
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                if (ReferencesAny(predicate.List, names, token)) { return true; }
                var inner = new HashSet<string>(names, StringComparer.Ordinal);
                inner.Remove(predicate.Variable);
                if (ReferencesAny(predicate.Predicate, inner, token)) { return true; }
                continue;
            }
            if (expression is GqlListComprehensionExpression comprehension)
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                if (ReferencesAny(comprehension.List, names, token)) { return true; }
                var inner = new HashSet<string>(names, StringComparer.Ordinal);
                inner.Remove(comprehension.Variable);
                if (comprehension.Predicate is not null && ReferencesAny(comprehension.Predicate, inner, token) ||
                    comprehension.Projection is not null && ReferencesAny(comprehension.Projection, inner, token)) { return true; }
                continue;
            }
            // Aggregate operands always belong to the original input scope.
            if (expression is GqlAggregateExpression) { continue; }
            string? name = expression switch
            {
                GqlVariableExpression variable => variable.Variable,
                GqlPropertyExpression property => property.Variable,
                GqlLabeledPredicate labeled => labeled.Variable,
                _ => null,
            };
            if (name is not null && names.Contains(name)) { return true; }
            foreach (var child in Children(expression)) { pending.Push(child); }
        }
        return false;
    }

    internal static bool ReferencesUnbound(GqlExpression root, IReadOnlyDictionary<string, object?> bindings, GraphCorrelatedBindings? correlated = null)
    {
        // The common inline literal/parameter/property cases need no traversal allocation.
        switch (root)
        {
            case GqlLiteralExpression or GqlParameterExpression: return false;
            case GqlVariableExpression variable: return !bindings.ContainsKey(variable.Variable);
            case GqlPropertyExpression property: return !bindings.ContainsKey(property.Variable);
            case GqlLabeledPredicate labeled: return !bindings.ContainsKey(labeled.Variable);
        }
        var pending = new Stack<GqlExpression>();
        pending.Push(root);
        while (pending.TryPop(out var expression))
        {
            if (expression is GqlExistsExpression exists)
            {
                if (correlated is null || !correlated.Subplans.TryGetValue(exists, out var subplan)) { return true; }
                foreach (string correlatedName in subplan.CorrelatedVariables)
                { if (!bindings.ContainsKey(correlatedName)) { return true; } }
                continue;
            }
            if (expression is GqlReduceExpression reduce)
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                if (ReferencesUnbound(reduce.List, bindings, correlated) || ReferencesUnbound(reduce.Initial, bindings, correlated)) { return true; }
                var inner = new GraphIteratorBinding(new GraphIteratorBinding(bindings, reduce.Accumulator), reduce.Variable);
                if (ReferencesUnbound(reduce.Body, inner, correlated)) { return true; }
                continue;
            }
            if (expression is GqlListPredicateExpression predicate)
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                if (ReferencesUnbound(predicate.List, bindings, correlated) ||
                    ReferencesUnbound(predicate.Predicate, new GraphIteratorBinding(bindings, predicate.Variable), correlated)) { return true; }
                continue;
            }
            if (expression is GqlListComprehensionExpression comprehension)
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                if (ReferencesUnbound(comprehension.List, bindings, correlated)) { return true; }
                var inner = new GraphIteratorBinding(bindings, comprehension.Variable);
                if (comprehension.Predicate is not null && ReferencesUnbound(comprehension.Predicate, inner, correlated) ||
                    comprehension.Projection is not null && ReferencesUnbound(comprehension.Projection, inner, correlated)) { return true; }
                continue;
            }
            string? name = expression switch
            {
                GqlVariableExpression variable => variable.Variable,
                GqlPropertyExpression property => property.Variable,
                GqlLabeledPredicate labeled => labeled.Variable,
                _ => null,
            };
            if (name is not null && !bindings.ContainsKey(name)) { return true; }
            foreach (var child in Children(expression)) { pending.Push(child); }
        }
        return false;
    }

    // Keep new yield state out of the existing iterator: its object size is paid by
    // every expression walk, including statements that contain no comprehensions.
    internal static IEnumerable<GqlExpression> Children(GqlExpression expression) => expression switch
    {
        GqlListComprehensionExpression comprehension => ComprehensionChildren(comprehension),
        GqlReduceExpression reduce => ReduceChildren(reduce),
        GqlListPredicateExpression predicate => PredicateChildren(predicate),
        GqlListSliceExpression slice => SliceChildren(slice),
        _ => OrdinaryChildren(expression),
    };

    private static IEnumerable<GqlExpression> ComprehensionChildren(GqlListComprehensionExpression comprehension)
    {
        if (string.IsNullOrEmpty(comprehension.Variable))
        { throw new GraphPlanningException("CG01017", ErrorMessages.IteratorNameRequired, comprehension.Location); }
        yield return comprehension.List;
        if (comprehension.Predicate is not null) { yield return comprehension.Predicate; }
        if (comprehension.Projection is not null) { yield return comprehension.Projection; }
    }

    private static IEnumerable<GqlExpression> ReduceChildren(GqlReduceExpression reduce)
    {
        ValidateReductionBindings(reduce.Accumulator, reduce.Variable, reduce);
        if (reduce.Initial is null || reduce.List is null || reduce.Body is null)
        { throw new GraphPlanningException("CG05025", ErrorMessages.ReductionBodyInvalid, reduce.Location); }
        yield return reduce.List;
        yield return reduce.Initial;
        yield return reduce.Body;
    }

    private static IEnumerable<GqlExpression> PredicateChildren(GqlListPredicateExpression predicate)
    {
        ValidateReductionBindings(null, predicate.Variable, predicate);
        if (!Enum.IsDefined(predicate.Kind) || predicate.List is null || predicate.Predicate is null)
        { throw new GraphPlanningException("CG05025", ErrorMessages.ReductionBodyInvalid, predicate.Location); }
        yield return predicate.List;
        yield return predicate.Predicate;
    }

    internal static void ValidateReductionBindings(string? accumulator, string variable, GqlExpression expression)
    {
        if (string.IsNullOrEmpty(variable) || expression is GqlReduceExpression &&
            (string.IsNullOrEmpty(accumulator) || accumulator == variable))
        { throw new GraphPlanningException("CG01018", ErrorMessages.ReductionBindingInvalid, expression.Location); }
    }

    private static IEnumerable<GqlExpression> SliceChildren(GqlListSliceExpression slice)
    {
        yield return slice.Operand;
        if (slice.From is not null) { yield return slice.From; }
        if (slice.To is not null) { yield return slice.To; }
    }

    private static IEnumerable<GqlExpression> OrdinaryChildren(GqlExpression expression)
    {
        switch (expression)
        {
            case GqlNamedFunctionCallExpression named:
                foreach (var argument in named.Arguments) { yield return argument; }
                break;
            case GqlFunctionCallExpression call:
                if (call.Arguments is null) { throw new GraphPlanningException("CG05001", ErrorMessages.ProjectionUnsupported, expression.Location); }
                foreach (var argument in call.Arguments) { yield return argument; }
                break;
            case GqlCastExpression cast: yield return cast.Operand; break;
            case GqlAggregateExpression { Operand: { } operand }: yield return operand; break;
            case GqlBinaryExpression binary: yield return binary.Left; yield return binary.Right; break;
            case GqlLogicalExpression logical:
                foreach (var operand in logical.Operands) { yield return operand; }
                break;
            case GqlUnaryExpression unary: yield return unary.Operand; break;
            case GqlIsNullExpression test: yield return test.Operand; break;
            case GqlTruthTestExpression test: yield return test.Operand; break;
            case GqlInExpression membership: yield return membership.Operand; yield return membership.List; break;
            case GqlStringPredicateExpression predicate: yield return predicate.Left; yield return predicate.Right; break;
            case GqlCaseExpression conditional:
                if (conditional.Alternatives is null || conditional.Alternatives.Count == 0 || conditional.Alternatives.Any(branch => branch is null || branch.Comparison is not ("=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" or "IS NULL" or "IS NOT NULL")))
                { throw new GraphPlanningException("CG05001", ErrorMessages.CaseInvalid, expression.Location); }
                if (conditional.Operand is not null) { yield return conditional.Operand; }
                foreach (var branch in conditional.Alternatives) { yield return branch.When; yield return branch.Then; }
                if (conditional.Otherwise is not null) { yield return conditional.Otherwise; }
                break;
            case GqlCoalesceExpression coalesce:
                if (coalesce.Operands is null || coalesce.Operands.Count < 2)
                { throw new GraphPlanningException("CG05001", ErrorMessages.CaseInvalid, expression.Location); }
                foreach (var operand in coalesce.Operands) { yield return operand; }
                break;
            case GqlNullIfExpression nullIf: yield return nullIf.Left; yield return nullIf.Right; break;
            case GqlListExpression list:
                if (list.Items is null) { throw new GraphPlanningException("CG05001", ErrorMessages.MembershipListRequired, expression.Location); }
                foreach (var item in list.Items) { yield return item; }
                break;
            case GqlMapExpression map:
                if (map.Entries is null || map.Entries.Any(entry => entry.Key is null) ||
                    map.Entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count() != map.Entries.Count)
                { throw new GraphPlanningException("CG05001", ErrorMessages.CompositeRequired, expression.Location); }
                foreach (var entry in map.Entries) { yield return entry.Value; }
                break;
            case GqlSubscriptExpression subscript: yield return subscript.Operand; yield return subscript.Index; break;
            case GqlMemberExpression member:
                if (member.Member is null) { throw new GraphPlanningException("CG05001", ErrorMessages.CompositeRequired, expression.Location); }
                yield return member.Operand; break;
        }
    }

    // Track group lists at their exact container depth. Accessing a group's entity is not
    // itself a group, while extracting a group from a list/map retains its provenance.
    internal static bool IsGroup(GqlExpression expression, Func<string, int, bool> variableGroup, int accesses, Func<GqlNamedFunctionCallExpression, GqlExpression?>? nativeExpression = null, GraphReductionAnalysis? analysis = null)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (analysis?.ScalarList(expression, null, out _) == true) { return false; }
        var pending = new Stack<(GqlExpression Expression, int Accesses)>();
        pending.Push((expression, accesses));
        while (pending.TryPop(out var item))
        {
            var (value, depth) = item;
            analysis?.Visit(value);
            switch (value)
            {
                case GqlReduceExpression reduce:
                    if (ReduceGroup(reduce, variableGroup, depth, nativeExpression, analysis)) { return true; }
                    break;
                case GqlListComprehensionExpression comprehension:
                    if (depth == 0) { break; }
                    if (ComprehensionGroup(comprehension, variableGroup, depth, nativeExpression, analysis)) { return true; }
                    break;
                case GqlListSliceExpression slice: pending.Push((slice.Operand, depth)); break;
                case GqlNamedFunctionCallExpression named:
                    if (nativeExpression?.Invoke(named) is { } native) { pending.Push((native, depth)); }
                    break;
                case GqlFunctionCallExpression { Function: GqlScalarFunction.Properties } call when depth > 0:
                    pending.Push((call.Arguments[0], depth));
                    break;
                case GqlFunctionCallExpression or GqlCastExpression: break;
                case GqlVariableExpression variable:
                    if (variableGroup(variable.Variable, depth)) { return true; }
                    break;
                case GqlPropertyExpression property:
                    if (variableGroup(property.Variable, depth + 1)) { return true; }
                    break;
                case GqlMemberExpression member: pending.Push((member.Operand, depth + 1)); break;
                case GqlSubscriptExpression subscript: pending.Push((subscript.Operand, depth + 1)); break;
                case GqlListExpression or GqlMapExpression when depth > 0:
                    foreach (var child in Children(value)) { pending.Push((child, depth - 1)); }
                    break;
                case GqlAggregateExpression { Function: GqlAggregateFunction.CollectList, Operand: { } operand } when depth > 0:
                    pending.Push((operand, depth - 1));
                    break;
                case GqlAggregateExpression { Function: GqlAggregateFunction.Min or GqlAggregateFunction.Max, Operand: { } operand }:
                    pending.Push((operand, depth));
                    break;
                case GqlCaseExpression or GqlCoalesceExpression or GqlNullIfExpression:
                    foreach (var child in ResultChildren(value)) { pending.Push((child, depth)); }
                    break;
            }
        }
        return false;
    }

    // Access consumes a container level; accessing an entity produces a scalar property.
    internal static bool MayReturn(GqlExpression expression, IReadOnlyDictionary<string, object?>? parameters, bool entity, Func<string, int, bool, bool>? variableShape = null, int initialAccesses = 0, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functionType = null, Func<GqlNamedFunctionCallExpression, GqlExpression?>? nativeExpression = null, GraphReductionAnalysis? analysis = null)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var pending = new Stack<(GqlExpression Expression, int Accesses)>();
        if (analysis?.ScalarList(expression, parameters, out _) == true) { return !entity && initialAccesses == 0; }
        pending.Push((expression, initialAccesses));
        while (pending.TryPop(out var item))
        {
            var (value, accesses) = item;
            analysis?.Visit(value);
            switch (value)
            {
                case GqlReduceExpression reduce:
                    if (ReduceMayReturn(reduce, parameters, entity, variableShape, accesses, functionType, nativeExpression, analysis)) { return true; }
                    break;
                case GqlListComprehensionExpression comprehension:
                    if (!entity && accesses == 0) { return true; }
                    if (ComprehensionMayReturn(comprehension, parameters, entity, variableShape, accesses, functionType, nativeExpression, analysis)) { return true; }
                    break;
                case GqlListSliceExpression slice: pending.Push((slice.Operand, accesses)); break;
                case GqlNamedFunctionCallExpression call:
                    if (nativeExpression?.Invoke(call) is { } native) { pending.Push((native, accesses)); break; }
                    var declared = (functionType?.Invoke(call) ?? GraphFunctionType.Any).At(accesses);
                    if (declared is null) { break; }
                    if (entity ? (declared.Kind & (GraphFunctionKind.Node | GraphFunctionKind.Relationship | GraphFunctionKind.Path)) != 0 ||
                        (declared.Kind & (GraphFunctionKind.List | GraphFunctionKind.Map)) != 0 && ContainsEntity(declared)
                        : (declared.Kind & (GraphFunctionKind.List | GraphFunctionKind.Map)) != 0) { return true; }
                    break;
                case GqlFunctionCallExpression call:
                    // Properties of stored entities are scalar. Properties(map) preserves the
                    // map's nested shapes, including entities, without returning an entity input.
                    if (call.Function == GqlScalarFunction.Properties && (entity || accesses > 0))
                    { pending.Push((call.Arguments[0], Math.Max(1, accesses))); }
                    if (entity ? GraphScalarFunctions.Entity(call.Function, accesses) != GraphProjectionKind.Scalar || accesses == 0 && call.Function is GqlScalarFunction.Nodes or GqlScalarFunction.Relationships
                        : GraphScalarFunctions.Composite(call.Function, accesses)) { return true; }
                    break;
                case GqlCastExpression: break;
                case GqlAggregateExpression aggregate:
                    if (aggregate.Function == GqlAggregateFunction.CollectList)
                    {
                        if (!entity && accesses == 0) { return true; }
                        if (aggregate.Operand is not null) { pending.Push((aggregate.Operand, Math.Max(0, accesses - 1))); }
                    }
                    else if (aggregate.Function is GqlAggregateFunction.Min or GqlAggregateFunction.Max && aggregate.Operand is not null)
                    { pending.Push((aggregate.Operand, accesses)); }
                    break;
                case GqlVariableExpression variable:
                    if (variableShape is not null ? variableShape(variable.Variable, accesses, entity) : entity && accesses == 0) { return true; }
                    break;
                case GqlPropertyExpression property:
                    if (variableShape?.Invoke(property.Variable, accesses + 1, entity) == true) { return true; }
                    break;
                case GqlMemberExpression member: pending.Push((member.Operand, accesses + 1)); break;
                case GqlSubscriptExpression subscript: pending.Push((subscript.Operand, accesses + 1)); break;
                case GqlListExpression or GqlMapExpression:
                    if (!entity && accesses == 0) { return true; }
                    foreach (var child in Children(value)) { pending.Push((child, Math.Max(0, accesses - 1))); }
                    break;
                case GqlParameterExpression parameter when !entity:
                    if (parameters is not null && parameters.TryGetValue(parameter.Name, out var binding) && MayContain(binding, accesses)) { return true; }
                    break;
                case GqlLiteralExpression literal when !entity:
                    if (MayContain(literal.Value, accesses)) { return true; }
                    break;
                case GqlCaseExpression or GqlCoalesceExpression or GqlNullIfExpression:
                    foreach (var child in ResultChildren(value)) { pending.Push((child, accesses)); }
                    break;
            }
        }
        return false;
    }

    // Proof is stricter than MayReturn: every possible non-NULL result must have
    // the same entity kind. Accesses retain this proof through projection boundaries.
    internal static GraphEntityShape EntityShape(GqlExpression expression, Func<string, int, GraphEntityShape> variableShape,
        int accesses, CancellationToken token, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functionType = null, Func<GqlNamedFunctionCallExpression, GqlExpression?>? nativeExpression = null, GraphReductionAnalysis? analysis = null)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (analysis?.ScalarList(expression, null, out _) == true) { return default; }
        var pending = new Stack<(GqlExpression Expression, int Depth, bool NonNull)>();
        pending.Push((expression, accesses, false));
        var kind = GraphProjectionKind.Scalar;
        bool optional = false;
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            var (value, depth, nonNull) = item;
            analysis?.Visit(value);
            switch (value)
            {
                case GqlReduceExpression reduce:
                    if (!Merge(ReduceEntity(reduce, variableShape, depth, token, functionType, nativeExpression, analysis), nonNull)) { return default; }
                    break;
                case GqlListComprehensionExpression comprehension:
                    if (depth == 0) { return default; }
                    if (!Merge(ComprehensionEntity(comprehension, variableShape, depth, token, functionType, nativeExpression, analysis), nonNull)) { return default; }
                    break;
                case GqlListSliceExpression slice: pending.Push((slice.Operand, depth, nonNull)); break;
                case GqlFunctionCallExpression { Function: GqlScalarFunction.Properties } call when depth > 0:
                    pending.Push((call.Arguments[0], depth, nonNull));
                    break;
                case GqlFunctionCallExpression call:
                    bool nullable = depth == 1 && call.Function is GqlScalarFunction.Nodes or GqlScalarFunction.Relationships
                        ? false : call.Function is GqlScalarFunction.StartNode or GqlScalarFunction.EndNode && call.Arguments is [GqlVariableExpression input]
                        ? variableShape(input.Variable, 0).Optional : true;
                    if (!Merge(new GraphEntityShape(GraphScalarFunctions.Entity(call.Function, depth), nullable), nonNull)) { return default; }
                    break;
                case GqlNamedFunctionCallExpression named:
                    if (nativeExpression?.Invoke(named) is { } native) { pending.Push((native, depth, nonNull)); break; }
                    var declared = functionType?.Invoke(named).At(depth);
                    var declaredKind = declared?.Kind == GraphFunctionKind.Node ? GraphProjectionKind.Node
                        : declared?.Kind == GraphFunctionKind.Relationship ? GraphProjectionKind.Relationship : GraphProjectionKind.Scalar;
                    if (!Merge(new GraphEntityShape(declaredKind, declared?.IsNullable ?? true), nonNull)) { return default; }
                    break;
                case GqlCastExpression: return default;
                case GqlVariableExpression variable:
                    if (!Merge(variableShape(variable.Variable, depth), nonNull)) { return default; }
                    break;
                case GqlPropertyExpression property:
                    if (!Merge(variableShape(property.Variable, depth + 1), nonNull)) { return default; }
                    break;
                case GqlMemberExpression member:
                    pending.Push((member.Operand, depth + 1, nonNull));
                    break;
                case GqlSubscriptExpression subscript:
                    optional |= !nonNull;
                    pending.Push((subscript.Operand, depth + 1, nonNull));
                    break;
                case GqlAggregateExpression { Function: GqlAggregateFunction.CollectList, Operand: { } operand }:
                    if (depth == 0) { return default; }
                    // COLLECT omits NULL operands, but retains NULLs inside a collected list.
                    pending.Push((operand, depth - 1, nonNull || depth == 1));
                    break;
                case GqlAggregateExpression { Function: GqlAggregateFunction.Min or GqlAggregateFunction.Max, Operand: { } operand }:
                    optional |= !nonNull;
                    pending.Push((operand, depth, nonNull));
                    break;
                case GqlListExpression list:
                    if (depth == 0 || list.Items.Count == 0) { return default; }
                    foreach (var child in list.Items) { pending.Push((child, depth - 1, nonNull)); }
                    break;
                case GqlMapExpression map:
                    if (depth == 0 || map.Entries.Count == 0) { return default; }
                    optional |= !nonNull;
                    foreach (var entry in map.Entries) { pending.Push((entry.Value, depth - 1, nonNull)); }
                    break;
                case GqlLiteralExpression { Value: null }:
                    optional |= !nonNull;
                    break;
                case GqlCaseExpression conditional:
                    optional |= conditional.Otherwise is null && !nonNull;
                    foreach (var child in ResultChildren(value)) { pending.Push((child, depth, nonNull)); }
                    break;
                case GqlNullIfExpression nullIf:
                    optional |= !nonNull;
                    pending.Push((nullIf.Left, depth, nonNull));
                    break;
                case GqlCoalesceExpression coalesce:
                    foreach (var child in coalesce.Operands) { pending.Push((child, depth, nonNull)); }
                    break;
                default:
                    return default;
            }
        }
        return new GraphEntityShape(kind, optional);

        bool Merge(GraphEntityShape shape, bool nonNull)
        {
            if (shape.Kind is not (GraphProjectionKind.Node or GraphProjectionKind.Relationship) ||
                kind != GraphProjectionKind.Scalar && kind != shape.Kind) { return false; }
            kind = shape.Kind;
            optional |= shape.Optional && !nonNull;
            return true;
        }
    }

    // A missing/NULL element has no known scalar type. Mixed container/scalar
    // branches defer container validation to evaluation (CG02013).
    internal static bool IsKnownScalar(GqlExpression expression, IReadOnlyDictionary<string, object?>? parameters,
        Func<string, int, bool> variableScalar, int accesses, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functionType = null, Func<GqlNamedFunctionCallExpression, GqlExpression?>? nativeExpression = null, GraphReductionAnalysis? analysis = null)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (analysis?.ScalarList(expression, parameters, out bool scalars) == true) { return accesses == 1 && scalars; }
        var pending = new Stack<(GqlExpression Expression, int Accesses)>();
        pending.Push((expression, accesses));
        while (pending.TryPop(out var item))
        {
            var (value, depth) = item;
            analysis?.Visit(value);
            switch (value)
            {
                case GqlReduceExpression reduce:
                    if (!ReduceScalar(reduce, parameters, variableScalar, depth, functionType, nativeExpression, analysis)) { return false; }
                    break;
                case GqlListComprehensionExpression comprehension:
                    if (depth == 0) { return false; }
                    if (!ComprehensionScalar(comprehension, parameters, variableScalar, depth, functionType, nativeExpression, analysis)) { return false; }
                    break;
                case GqlListSliceExpression slice: pending.Push((slice.Operand, depth)); break;
                case GqlFunctionCallExpression { Function: GqlScalarFunction.Properties } call when depth > 0:
                    pending.Push((call.Arguments[0], depth));
                    break;
                case GqlFunctionCallExpression call:
                    if (depth == 0 ? (GraphScalarFunctions.Result(call.Function) & GraphFunctionKind.Scalar) == 0
                        : depth != 1 || call.Function is not (GqlScalarFunction.Labels or GqlScalarFunction.Keys)) { return false; }
                    break;
                case GqlNamedFunctionCallExpression named:
                    if (nativeExpression?.Invoke(named) is { } native) { pending.Push((native, depth)); break; }
                    var declared = functionType?.Invoke(named).At(depth);
                    if (declared is null || (declared.Kind & ~GraphFunctionKind.Scalar) != 0) { return false; }
                    break;
                case GqlCastExpression:
                    if (depth != 0) { return false; }
                    break;
                case GqlVariableExpression variable:
                    if (!variableScalar(variable.Variable, depth)) { return false; }
                    break;
                case GqlPropertyExpression property:
                    if (!variableScalar(property.Variable, depth + 1)) { return false; }
                    break;
                case GqlMemberExpression member: pending.Push((member.Operand, depth + 1)); break;
                case GqlSubscriptExpression subscript: pending.Push((subscript.Operand, depth + 1)); break;
                case GqlLiteralExpression literal:
                    if (!ScalarValue(literal.Value, depth)) { return false; }
                    break;
                case GqlParameterExpression parameter:
                    if (parameters is null || !parameters.TryGetValue(parameter.Name, out var binding) || !ScalarValue(binding, depth)) { return false; }
                    break;
                case GqlListExpression or GqlMapExpression:
                    var children = Children(value).ToArray();
                    if (depth == 0 || children.Length == 0) { return false; }
                    foreach (var child in children) { pending.Push((child, depth - 1)); }
                    break;
                case GqlAggregateExpression { Function: GqlAggregateFunction.CollectList, Operand: { } operand }:
                    if (depth == 0) { return false; }
                    pending.Push((operand, depth - 1));
                    break;
                case GqlAggregateExpression { Function: GqlAggregateFunction.Min or GqlAggregateFunction.Max, Operand: { } operand }:
                    pending.Push((operand, depth));
                    break;
                case GqlCaseExpression or GqlCoalesceExpression or GqlNullIfExpression:
                    foreach (var child in ResultChildren(value)) { pending.Push((child, depth)); }
                    break;
                default:
                    if (depth != 0) { return false; }
                    break;
            }
        }
        return true;
    }

    // Keep captured lexical callbacks on the comprehension-only path. Ordinary expressions
    // retain their existing allocation shape and iterative traversal.
    private static bool ComprehensionGroup(GqlListComprehensionExpression expression, Func<string, int, bool> outer,
        int depth, Func<GqlNamedFunctionCallExpression, GqlExpression?>? native, GraphReductionAnalysis? analysis)
        => IsGroup(expression.Projection ?? new GqlVariableExpression(expression.Variable),
            (name, access) => name == expression.Variable ? IsGroup(expression.List, outer, access + 1, native, analysis) : outer(name, access), depth - 1, native, analysis);

    private static bool ComprehensionMayReturn(GqlListComprehensionExpression expression, IReadOnlyDictionary<string, object?>? parameters,
        bool entity, Func<string, int, bool, bool>? outer, int depth, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functions,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native, GraphReductionAnalysis? analysis)
        => MayReturn(expression.Projection ?? new GqlVariableExpression(expression.Variable), parameters, entity,
            (name, access, entities) => name == expression.Variable ? MayReturn(expression.List, parameters, entities, outer, access + 1, functions, native, analysis)
                : outer?.Invoke(name, access, entities) ?? entities && access == 0, Math.Max(0, depth - 1), functions, native, analysis);

    private static GraphEntityShape ComprehensionEntity(GqlListComprehensionExpression expression, Func<string, int, GraphEntityShape> outer,
        int depth, CancellationToken token, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functions,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native, GraphReductionAnalysis? analysis)
        => EntityShape(expression.Projection ?? new GqlVariableExpression(expression.Variable),
            (name, access) => name == expression.Variable ? EntityShape(expression.List, outer, access + 1, token, functions, native, analysis) : outer(name, access),
            depth - 1, token, functions, native, analysis);

    private static bool ComprehensionScalar(GqlListComprehensionExpression expression, IReadOnlyDictionary<string, object?>? parameters,
        Func<string, int, bool> outer, int depth, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functions,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native, GraphReductionAnalysis? analysis)
        => IsKnownScalar(expression.Projection ?? new GqlVariableExpression(expression.Variable), parameters,
            (name, access) => name == expression.Variable ? IsKnownScalar(expression.List, parameters, outer, access + 1, functions, native, analysis) : outer(name, access),
            depth - 1, functions, native, analysis);

    private static bool ContainsEntity(GraphFunctionType type)
    {
        for (GraphFunctionType? current = type; current is not null; current = current.Element)
        {
            if ((current.Kind & (GraphFunctionKind.Node | GraphFunctionKind.Relationship | GraphFunctionKind.Path)) != 0) { return true; }
            if (current.Element is null && (current.Kind & (GraphFunctionKind.List | GraphFunctionKind.Map)) != 0) { return true; }
        }
        return false;
    }

    private static bool ScalarValue(object? value, int accesses)
    {
        var pending = new Stack<(object? Value, int Accesses)>();
        pending.Push((value, accesses));
        while (pending.TryPop(out var item))
        {
            if (item.Accesses == 0)
            {
                if (item.Value is null || GraphCompositeValues.IsComposite(item.Value)) { return false; }
            }
            else if (item.Value is object?[] { Length: > 0 } list)
            { foreach (var child in list) { pending.Push((child, item.Accesses - 1)); } }
            else if (item.Value is IReadOnlyDictionary<string, object?> { Count: > 0 } map)
            { foreach (var child in map.Values) { pending.Push((child, item.Accesses - 1)); } }
            else { return false; }
        }
        return true;
    }

    private static bool MayContain(object? value, int accesses)
    {
        var pending = new Stack<(object? Value, int Accesses)>(); pending.Push((value, accesses));
        while (pending.TryPop(out var item))
        {
            if (item.Accesses == 0)
            {
                if (GraphCompositeValues.IsComposite(item.Value) || item.Value is byte or ushort or uint or ulong) { return true; }
            }
            else if (item.Value is object?[] list)
            { foreach (var child in list) { pending.Push((child, item.Accesses - 1)); } }
            else if (item.Value is IReadOnlyDictionary<string, object?> map)
            { foreach (var child in map.Values) { pending.Push((child, item.Accesses - 1)); } }
        }
        return false;
    }

    // Only value-producing branches propagate entities: comparisons and tests produce scalars.
    internal static IEnumerable<GqlExpression> ResultChildren(GqlExpression expression)
    {
        if (expression is GqlCaseExpression conditional)
        {
            foreach (var branch in conditional.Alternatives) { yield return branch.Then; }
            if (conditional.Otherwise is not null) { yield return conditional.Otherwise; }
        }
        else if (expression is GqlNullIfExpression nullIf) { yield return nullIf.Left; }
        else if (expression is GqlListExpression or GqlMapExpression or GqlCoalesceExpression or GqlSubscriptExpression or GqlMemberExpression)
        { foreach (var child in Children(expression)) { yield return child; } }
    }
}

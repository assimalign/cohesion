using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>JSON execution reachable through bound calls and domain conversions.</summary>
internal static class SqlJsonUsage
{
    internal static bool Contains(SqlBoundExpression? expression, Collation collation, CancellationToken token)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        // This walk runs on ordinary plans too. Avoid iterator/closure allocations,
        // and follow the exact bound function/domain version retained by the plan.
        switch (expression)
        {
            case SqlBoundJsonFunction: return true;
            case SqlBoundBinary binary: return Child(binary.Left) || Child(binary.Right);
            case SqlBoundUnary unary: return Child(unary.Operand);
            case SqlBoundLogical logical: return Children(logical.Operands);
            case SqlBoundBitwise bitwise: return Child(bitwise.Left) || Child(bitwise.Right);
            case SqlBoundDistinct distinct: return Child(distinct.Left) || Child(distinct.Right);
            case SqlBoundJsonAccess access:
                if (Child(access.Operand)) { return true; }
                foreach (var step in access.Steps) { if (Child(step.Selector)) { return true; } }
                return false;
            case SqlBoundIsJson json: return Child(json.Operand);
            case SqlBoundBooleanTest test: return Child(test.Operand);
            case SqlBoundIsNull isNull: return Child(isNull.Operand);
            case SqlBoundBetween between: return Child(between.LowOperand) || Child(between.HighOperand) || Child(between.Low) || Child(between.High);
            case SqlBoundIn member: return Child(member.Operand) || Children(member.Candidates);
            case SqlBoundInSubquery member: return Child(member.Operand);
            case SqlBoundQuantifiedComparison quantified: return Child(quantified.Operand);
            case SqlBoundLike like: return Child(like.Operand) || Child(like.Pattern) || Child(like.Escape);
            case SqlBoundCall call: return Function(call.Function, call.Collation.IsFixed ? call.Collation.Resolve(null) : collation) || Children(call.Arguments);
            case SqlBoundSequence sequence: return Children(sequence.Arguments);
            case SqlBoundSpecialForm form: return Children(form.Arguments);
            case SqlBoundCoalesce coalesce: return Children(coalesce.Arguments);
            case SqlBoundCase conditional:
                if (Child(conditional.Input) || Child(conditional.ElseResult)) { return true; }
                foreach (var when in conditional.Whens) { if (Child(when.Condition) || Child(when.Result)) { return true; } }
                return false;
            case SqlBoundCast cast: return Function(cast.Target.Check, cast.Collation) || Child(cast.Operand);
            case SqlBoundDomainArgument domain: return Function(domain.Check, domain.FixedCollation ?? collation) || Child(domain.Operand);
            case SqlBoundDefault value: return Child(value.Value);
            default: return false;
        }

        bool Child(SqlBoundExpression? child) => Contains(child, collation, token);
        bool Children(IReadOnlyList<SqlBoundExpression> children)
        {
            for (int index = 0; index < children.Count; index++) { if (Child(children[index])) { return true; } }
            return false;
        }
        bool Function(SqlScalarFunction? function, Collation inputCollation) => FunctionContains(function, inputCollation, token);
    }

    internal static bool FunctionContains(SqlScalarFunction? function, Collation collation, CancellationToken token)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (function is null) { return false; }
        if (function is SqlExpressionFunction body && (body.PreparedBody(collation) is not { } prepared || Contains(prepared, collation, token)) ||
            FunctionContains(function.ReturnType.Check, collation, token)) { return true; }
        for (int index = 0; index < function.Parameters.Count; index++)
        { if (FunctionContains(function.Parameters[index].Check, collation, token)) { return true; } }
        return false;
    }

    // Schema plans retain syntax until their ordinary admission binder runs. Inspect
    // metadata without compiling a body or invoking a constant application callback.
    internal static bool ContainsSyntax(SqlExpression? expression, SqlFunctionEnvironment functions, Collation collation, CancellationToken token)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is null) { return false; }
        if (expression is SqlJsonFunctionExpression) { return true; }
        if (expression is SqlCastExpression cast && FunctionContains(functions.Types.Resolve(cast.TargetType, out _)?.Check, collation, token)) { return true; }
        if (expression is SqlFunctionCallExpression call && functions.Catalog.TryGetOverloads(call.FunctionName, out var overloads))
        {
            foreach (var function in overloads)
            {
                if (function is SqlScalarFunction scalar && FunctionContains(scalar, collation, token)) { return true; }
                foreach (var parameter in function.Parameters)
                { if (FunctionContains(parameter.Check, collation, token)) { return true; } }
            }
        }
        foreach (var child in SqlPlanner.Children(expression))
        { if (ContainsSyntax(child, functions, collation, token)) { return true; } }
        return false;
    }
}

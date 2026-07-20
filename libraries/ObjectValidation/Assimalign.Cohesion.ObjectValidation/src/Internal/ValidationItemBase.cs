using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal abstract class ValidationItemBase<T, TValue> : IValidationItem<T, TValue>
{
    private string expressionBody;
    private Expression<Func<T, TValue>> expression;

    public ValidationItemBase()
    {
        this.ItemRuleStack ??= new ValidationRuleQueue();
    }

    public Expression<Func<T, TValue>> ItemExpression
    {
        get => expression;
        set
        {
            // Only member expressions are supported for validation
            if (value.Body is MemberExpression)
            {
                this.expression = value;
                this.expressionBody = expression.ToString();
            }
            else
            {
                throw new ValidationInvalidMemberException(value);
            }
        }
    }

    public Func<T, bool> ValidationCondition { get; set; }

    public IValidationRuleQueue ItemRuleStack { get; }

    public abstract void Evaluate(IValidationContext context);

    public virtual TValue GetValue(T instance)
    {
        try
        {
            // Read the member (or member chain) by walking the expression tree's already-resolved
            // metadata directly, rather than compiling a delegate. Expression.Compile emits IL at run
            // time and is unavailable under NativeAOT (IL3050); walking the resolved PropertyInfo/
            // FieldInfo is reflection-only and preserves the same member-access and null-in-chain
            // semantics (a null owner throws and is caught below, yielding default).
            return (TValue)EvaluateMember(expression.Body, instance);
        }
        catch // Null Reference Exceptions tend to be thrown when chained members in a type are null.
        {
            return default(TValue);
        }
    }

    public override string ToString() => this.expressionBody;

    private static object EvaluateMember(Expression node, object instance)
    {
        switch (node)
        {
            case ParameterExpression:
                return instance;

            case MemberExpression member:
                object owner = EvaluateMember(member.Expression, instance);

                return member.Member switch
                {
                    PropertyInfo property => property.GetValue(owner),
                    FieldInfo field => field.GetValue(owner),
                    _ => null
                };

            case UnaryExpression unary when unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked:
                return EvaluateMember(unary.Operand, instance);

            default:
                return null;
        }
    }
}

using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

[global::System.Diagnostics.DebuggerDisplay("ValidationItem")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(ValidationItem<,>.DebugView))]
internal sealed class ValidationItem<T, TValue> : ValidationItemBase<T, TValue>
{
    public override void Evaluate(IValidationContext context)
    {
        if (context.Instance is not T instance)
        {
            return;
        }
        if (this.ValidationCondition is not null && !this.ValidationCondition.Invoke(instance))
        {
            return;
        }

        var value = this.GetValue(instance);

        // The chain stops on this member's own failure only. The context also holds the errors of every
        // member evaluated before this one, and those must not keep this member's rules from running:
        // stopping across members is ValidationMode.Stop, which the validator applies between items.
        var failed = false;

        foreach (var rule in this.ItemRuleStack)
        {
            if (failed && !context.ContinueThroughValidationChain)
            {
                break;
            }
            if (rule is ValidationRuleBase<TValue> ruleBase)
            {
                ruleBase.ParentContext = context;
            }

            // A timestamp per evaluation, never state on the item: the item is shared by every validation
            // that uses its profile, concurrently in a server.
            long started = Stopwatch.GetTimestamp();

            if (rule.TryValidate(value, out var ruleContext))
            {
                var errors = ruleContext is ValidationContext<TValue> builtIn ? builtIn.GetErrors() : ruleContext.Errors;
                foreach (var error in errors)
                {
                    context.AddFailure(error);
                    failed = true;
                }

                context.AddInvocation(new ValidationInvocation(rule.Name, true, Stopwatch.GetElapsedTime(started).Ticks));
            }
            else
            {
                context.AddInvocation(new ValidationInvocation(rule.Name, false, Stopwatch.GetElapsedTime(started).Ticks));
            }
        }
    }

    private sealed class DebugView
    {
        private readonly ValidationItem<T, TValue> _target;

        public DebugView(ValidationItem<T, TValue> value)
        {
            _target = value;
        }

        public global::System.Linq.Expressions.Expression<global::System.Func<T, TValue>> ItemExpression => _target.ItemExpression;
        public global::System.Func<T, bool> ValidationCondition => _target.ValidationCondition;
        public global::Assimalign.Cohesion.ObjectValidation.IValidationRuleQueue ItemRuleStack => _target.ItemRuleStack;
    }
}

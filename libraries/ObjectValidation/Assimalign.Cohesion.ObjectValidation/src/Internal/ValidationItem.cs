using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

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
                foreach (var error in ruleContext.Errors)
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
}
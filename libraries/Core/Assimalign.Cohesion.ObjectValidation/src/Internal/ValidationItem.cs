using System;
using System.Diagnostics;
using System.Linq;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal sealed class ValidationItem<T, TValue> : ValidationItemBase<T, TValue>
{
    private readonly Stopwatch stopwatch;

    public ValidationItem()
    {
        this.stopwatch = SimpleObjectPool.Rent<Stopwatch>();
    }

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

        foreach (var rule in this.ItemRuleStack)
        {
            if (!context.ContinueThroughValidationChain && context.Errors.Any())
            {
                break;
            }
            if (rule is ValidationRuleBase<TValue> ruleBase)
            {
                ruleBase.ParentContext = context;
            }

            stopwatch.Restart();

            if (rule.TryValidate(value, out var ruleContext))
            {
                foreach (var error in ruleContext.Errors)
                {
                    context.AddFailure(error);
                }

                stopwatch.Stop();
                context.AddInvocation(new ValidationInvocation(rule.Name, true, stopwatch.ElapsedTicks));
            }
            else
            {
                stopwatch.Stop();
                context.AddInvocation(new ValidationInvocation(rule.Name, false, stopwatch.ElapsedTicks));
            }
        }

        stopwatch.Reset();
    }
}
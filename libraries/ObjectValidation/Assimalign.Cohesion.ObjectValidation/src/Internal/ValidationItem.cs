using System;
using System.Diagnostics;
using System.Linq;

namespace Assimalign.Cohesion.ObjectValidation.Internal;

internal sealed class ValidationItem<T, TValue> : ValidationItemBase<T, TValue>
{
    private readonly Stopwatch _stopwatch;

    public ValidationItem()
    {
        this._stopwatch = SimpleObjectPool.Rent<Stopwatch>();
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

            _stopwatch.Restart();

            if (rule.TryValidate(value, out var ruleContext))
            {
                foreach (var error in ruleContext.Errors)
                {
                    context.AddFailure(error);
                }

                _stopwatch.Stop();
                context.AddInvocation(new ValidationInvocation(rule.Name, true, _stopwatch.ElapsedTicks));
            }
            else
            {
                _stopwatch.Stop();
                context.AddInvocation(new ValidationInvocation(rule.Name, false, _stopwatch.ElapsedTicks));
            }
        }

        _stopwatch.Reset();
    }
}
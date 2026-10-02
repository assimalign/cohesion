using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.ObjectValidation.Internal;


internal sealed class ValidationItemCollection<T, TValue> : ValidationItemBase<T, IEnumerable<TValue>>
{
    private readonly Type _paramType;

    public ValidationItemCollection()
    {
        this._paramType = typeof(T);
    }

    public override void Evaluate(IValidationContext context)
    {
        if (context.Instance is T instance)
        {
            if (this.ValidationCondition is not null && !this.ValidationCondition.Invoke(instance))
            {
                return;
            }

            var value = this.GetValue(instance);
            var stopwatch = new Stopwatch();

            // The chain stops on this member's own failure only, as in ValidationItem: errors other
            // members reported earlier must not keep this member's rules from running. A rule runs over
            // every element, so the rule that fails first reports each failing element before the chain
            // stops.
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

                stopwatch.Restart();

                if (value is not null && value is IEnumerable<TValue> enumerable)
                {
                    foreach (var enumValue in enumerable)
                    {
                        if (rule.TryValidate(enumValue, out var ruleContext))
                        {
                            foreach (var error in ruleContext.Errors)
                            {
                                context.AddFailure(error);
                                failed = true;
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
                }
                else
                {
                    stopwatch.Stop();
                    context.AddInvocation(new ValidationInvocation(rule.Name, false, stopwatch.ElapsedTicks)
                    {
                        InvocationErrorMessage = $"The following enumerable expression: '{this.ItemExpression}' returned null for instance '{this._paramType.Name}'."
                    });
                }
            }
        }
    }
}


//var tokenSource = new CancellationTokenSource();
//var parallelOptions = new ParallelOptions()
//{
//    CancellationToken = tokenSource.Token
//};

//var results = Parallel.ForEach(this.ItemRuleStack, parallelOptions, (rule, state, index) =>
//{
//    var stopwatch = new Stopwatch();

//    if (this.ItemValidationMode == ValidationMode.Stop && context.Errors.Any())
//    {
//        tokenSource.Cancel();
//    }

//    stopwatch.Start();
//    if (value is not null && value is IEnumerable<TValue> enumerable)
//    {
//        foreach (var enumValue in enumerable)
//        {
//            if (rule.TryValidate(enumValue, out var ruleContext))
//            {
//                foreach (var error in ruleContext.Errors)
//                {
//                    context.AddFailure(error);
//                }

//                stopwatch.Stop();
//                context.AddInvocation(new ValidationInvocation(rule.Name, true, stopwatch.ElapsedTicks));
//            }
//            else
//            {
//                stopwatch.Stop();
//                context.AddInvocation(new ValidationInvocation(rule.Name, false, stopwatch.ElapsedTicks));
//            }
//        }
//    }
//    else
//    {
//        stopwatch.Stop();
//        context.AddInvocation(new ValidationInvocation(rule.Name, false, stopwatch.ElapsedTicks));
//    }
//});
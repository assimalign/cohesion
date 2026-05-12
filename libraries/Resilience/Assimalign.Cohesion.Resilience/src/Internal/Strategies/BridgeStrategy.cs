//using System;
//using System.Collections.Generic;
//using System.Text;
//using System.Threading.Tasks;

//namespace Assimalign.Cohesion.Resilience.Internal;

//internal class BridgeStrategy : Strategy
//{
//    public override ValueTask ExecuteAsync<TState>(
//        ResilienceStrategyCallback<TState> callback, 
//        IResilienceContext context, 
//        TState state)
//    {
//    }
//    public override ValueTask<TResult> ExecuteAsync<TResult, TState>(
//        ResilienceStrategyCallback<TResult, TState> callback, 
//        IResilienceContext context, 
//        TState state)
//    {
//    }
//    protected static Outcome<TTo> ConvertOutcome<TFrom, TTo>(Outcome<TFrom> outcome)
//    {
//        return outcome.Match<TTo>(result =>
//        {
//            if (result is TTo to)
//            {
//                return to;
//            }
//            throw new InvalidCastException("");
//        });
//    }
//    public override ValueTask DisposeAsync()
//    {
//        throw new NotImplementedException();
//    }
//}

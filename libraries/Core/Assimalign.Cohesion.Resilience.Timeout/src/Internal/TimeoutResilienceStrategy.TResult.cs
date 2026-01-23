using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class TimeoutResilienceStrategy<TResult> : TimeoutResilienceStrategyBase, IResilienceStrategy<TResult>
{
    public TimeoutResilienceStrategy(TimeoutStrategyOptions options) : base(options)
    {

    }

    public ValueTask<TResult> ExecuteAsync<TState>(ResilienceStrategyCallback<TResult, TState> callback, IResilienceContext context, TState state)
    {
        throw new NotImplementedException();
    }
}

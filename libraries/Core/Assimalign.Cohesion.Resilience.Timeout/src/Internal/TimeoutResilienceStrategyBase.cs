using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal abstract class TimeoutResilienceStrategyBase
{
    protected TimeoutResilienceStrategyBase(TimeoutStrategyOptions options)
    {
        DefaultTimeout = options.Timeout;
        TimeoutGenerator = options.TimeoutGenerator;
        OnTimeout = options.OnTimeout;
        CancellationTokenSourcePool = new CancellationTokenSourcePool(options.TimeProvider);
    }
    protected TimeSpan DefaultTimeout { get; }
    protected Func<TimeoutGeneratorArguments, ValueTask<TimeSpan>>? TimeoutGenerator { get; }
    protected Func<OnTimeoutArguments, ValueTask>? OnTimeout { get; }
    protected CancellationTokenSourcePool CancellationTokenSourcePool { get; }


    protected static CancellationTokenRegistration CreateRegistration(CancellationTokenSource cancellationSource, CancellationToken previousToken)
    {
        return previousToken.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), cancellationSource);
    }
}

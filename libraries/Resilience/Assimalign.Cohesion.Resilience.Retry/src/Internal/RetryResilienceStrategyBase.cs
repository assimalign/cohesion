using System;

namespace Assimalign.Cohesion.Resilience.Internal;

internal abstract class RetryResilienceStrategyBase
{
    internal RetryResilienceStrategyBase(
        int retryCount,
        TimeProvider timeProvider,
        Func<double> randomizer,
        TimeSpan delay,
        TimeSpan? maxDelay,
        bool useJitter,
        DelayBackoffType backoffType)
    {
        RetryCount = retryCount;
        TimeProvider = timeProvider;
        Randomizer = randomizer;
        Delay = delay;
        MaxDelay = maxDelay;
        UseJitter = useJitter;
        BackoffType = backoffType;
    }


    protected readonly int RetryCount;
    protected readonly TimeProvider TimeProvider;
    protected readonly Func<double> Randomizer;
    protected readonly TimeSpan Delay;
    protected readonly TimeSpan? MaxDelay;
    protected readonly bool UseJitter;
    protected readonly DelayBackoffType BackoffType;




    internal bool IsLastAttempt(int attempt, out bool incrementAttempts)
    {
        if (attempt == int.MaxValue)
        {
            incrementAttempts = false;
            return false;
        }

        incrementAttempts = true;
        return attempt >= RetryCount;
    }
}

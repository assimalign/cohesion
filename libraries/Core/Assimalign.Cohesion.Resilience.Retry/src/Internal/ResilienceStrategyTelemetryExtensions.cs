using Assimalign.Cohesion.Resilience.Telemetry;
using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Resilience.Retry.Internal;

internal static class ResilienceStrategyTelemetryExtensions
{
    extension(ResilienceStrategyTelemetry telemetry)
    {
        internal void Report<TArgs, TResult>(ResilienceEvent resilienceEvent, TArgs args)
        where TArgs : IOutcomeArguments<TResult>
        {
            if (telemetry.Listener is null || resilienceEvent.Severity == ResilienceEventSeverity.None)
            {
                return;
            }

            Listener.Write<TResult, TArgs>(new(TelemetrySource, resilienceEvent, args.Context, args, args.Outcome));
        }
    }
}

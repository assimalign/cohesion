using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Text;

namespace Assimalign.Cohesion.Resilience.Internal;

[EventSource(Name = "")]
internal class RetryResilienceEventSource : EventSource
{

    public static RetryResilienceEventSource Log { get; } = new RetryResilienceEventSource();


    [Event(1, Level = EventLevel.Informational)]
    public void FinalExecutionAttempt()
    {

    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Text;

namespace Assimalign.Cohesion.Resilience.Internal;

[EventSource(Name = "Assimalign.Cohesion.Resilience.TimeoutResilienceEventSource")]
internal class TimeoutResilienceEventSource : EventSource
{
}

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Text;

namespace Assimalign.Cohesion.Resilience.Internal;


[EventSource(Name = nameof(Assimalign) + nameof(Cohesion) + nameof(Resilience))]
internal class ResilienceEventSource : EventSource
{
    public const string name = nameof(Assimalign) + nameof(Cohesion) + nameof(Resilience);

    public ResilienceEventSource()
    {
    }


    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}


public static class ResilienceAppEnvironment
{

    extension(ResilienceEventSource environment)
    {
        
    }
}
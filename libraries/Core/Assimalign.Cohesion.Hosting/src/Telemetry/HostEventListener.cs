using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Hosting;

public abstract class HostEventListener
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="args"></param>
    public abstract void Write(in HostEventArgs args);


    public static HostEventListener Create(IEnumerable<HostEventListener> listeners)
    {
        ArgumentNullException.ThrowIfNull(listeners);

        return new Listener(listeners);
    }

    partial class Listener : HostEventListener
    {
        private readonly HostEventListener[] _listeners;

        // Metrics 
        //private static readonly Meter Meter = new(nameof(Assimalign.Cohesion.Hosting), "1.0");
        //private readonly Histogram<double> _execution;

        public Listener(IEnumerable<HostEventListener> listeners)
        {
            _listeners = listeners.ToArray();
            //_execution = Meter.CreateHistogram<double>(
            //    "resilience.polly.pipeline.duration",
            //    unit: "ms",
            //    description: "The execution duration of resilience pipelines.");

            //_execution.Record()
        }
        public override void Write(in HostEventArgs args)
        {
            for (int i = 0; i < _listeners.Length; i++)
            {
                _listeners[i].Write(args);
            }
        }
    }
}

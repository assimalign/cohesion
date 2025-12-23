using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Transports;

public class TransportOptions
{
    public TransportOptions()
    {
        EventListeners = new List<TransportEventListener>();
    }

    /// <summary>
    /// Returns a  collection of event listeners.
    /// </summary>
    public List<TransportEventListener> EventListeners { get; }
}

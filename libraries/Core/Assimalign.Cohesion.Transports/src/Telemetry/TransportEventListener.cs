using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// 
/// </summary>
public abstract class TransportEventListener
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TArgs"></typeparam>
    /// <param name="args"></param>
    public abstract void Write<TArgs>(in TransportEventArgs<TArgs> args);

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TArgs"></typeparam>
    /// <param name="args"></param>
    public abstract void Write<TArgs>(in TransportConnectionEventArgs<TArgs> args);


    public static TransportEventListener Create(IEnumerable<TransportEventListener> listeners)
    {
        return new Listener(listeners);
    }

    partial class Listener : TransportEventListener
    {
        private readonly TransportEventListener[] _listeners;
        public Listener(IEnumerable<TransportEventListener> listeners)
        {
            _listeners = listeners.ToArray();
        }
        public override void Write<TArgs>(in TransportEventArgs<TArgs> args)
        {
            for (int i = 0; i < _listeners.Length; i++)
            {
                _listeners[i].Write(args);
            }
        }
        public override void Write<TArgs>(in TransportConnectionEventArgs<TArgs> args)
        {
            for (int i = 0; i < _listeners.Length; i++)
            {
                _listeners[i].Write(args);
            }
        }
    }
}

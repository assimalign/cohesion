using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>Retains endpoint readers for listeners composed by Web.Hosting.</summary>
internal sealed class WebServerEndPoints
{
    // The transport options have no endpoint metadata seam. Associate only the host's
    // composition with its readers, without extending the public transport contracts.
    private static readonly ConditionalWeakTable<HttpConnectionListenerOptions, WebServerEndPoints> _captures = new();
    private readonly List<Func<EndPoint>> _readers = new();

    public static WebServerEndPoints Capture(HttpConnectionListenerOptions options)
        => _captures.GetValue(options, static _ => new WebServerEndPoints());

    public Func<EndPoint>[] Snapshot() => _readers.ToArray();

    public static IConnectionListener Track(HttpConnectionListenerOptions options, IConnectionListener listener)
    {
        if (_captures.TryGetValue(options, out WebServerEndPoints? capture))
        {
            capture._readers.Add(() => listener.EndPoint);
        }
        return listener;
    }

    public static IMultiplexedConnectionListener Track(HttpConnectionListenerOptions options, IMultiplexedConnectionListener listener)
    {
        if (_captures.TryGetValue(options, out WebServerEndPoints? capture))
        {
            capture._readers.Add(() => listener.EndPoint);
        }
        return listener;
    }
}

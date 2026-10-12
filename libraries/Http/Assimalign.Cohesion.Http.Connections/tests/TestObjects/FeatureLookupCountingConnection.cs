using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>Counts feature accesses without advertising a directional-abort contract on the connection.</summary>
internal sealed class FeatureLookupCountingConnection : IConnection
{
    private readonly TestConnection _inner;
    private readonly PipeReader _input;
    private readonly CountingFeatureCollection _features = new();

    public FeatureLookupCountingConnection(byte[] input, bool hasDirectionalAbort, PipeReader? inputReader = null)
    {
        _inner = new TestConnection(input);
        _input = inputReader ?? _inner.Input;
        if (hasDirectionalAbort)
        {
            _features.Set(new DirectionalAbortFeature());
        }
    }

    public int FeatureAccesses { get; private set; }

    public int FeatureEnumerations => _features.Enumerations;

    public IConnectionFeatureCollection Features
    {
        get
        {
            FeatureAccesses++;
            return _features;
        }
    }

    public ConnectionId Id => _inner.Id;
    public EndPoint? LocalEndPoint => _inner.LocalEndPoint;
    public EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
    public PipeReader Input => _input;
    public PipeWriter Output => _inner.Output;
    public ConnectionDirection Direction => _inner.Direction;
    public ConnectionCapabilities Capabilities => _inner.Capabilities;
    public ConnectionState State => _inner.State;
    public CancellationToken ConnectionClosed => _inner.ConnectionClosed;
    public void Abort(Exception? reason = null) => _inner.Abort(reason);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private sealed class CountingFeatureCollection : IConnectionFeatureCollection
    {
        private readonly ConnectionFeatureCollection _inner = new();

        public int Enumerations { get; private set; }

        public IConnectionFeature? Get(string name) => _inner.Get(name);

        public void Set<TFeature>(TFeature feature) where TFeature : class, IConnectionFeature => _inner.Set(feature);

        public bool Remove(string name) => _inner.Remove(name);

        public IEnumerator<IConnectionFeature> GetEnumerator()
        {
            Enumerations++;
            return _inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DirectionalAbortFeature : IMultiplexedStreamAbort
    {
        public string Name => IMultiplexedStreamAbort.FeatureName;

        public void AbortRead(long errorCode) { }

        public void AbortWrite(long errorCode) { }
    }
}

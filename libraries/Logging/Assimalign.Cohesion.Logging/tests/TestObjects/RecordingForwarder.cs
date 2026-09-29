using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Logging.Tests;

/// <summary>
/// Forwarder that records the factory it was created with and appends <c>forwarder:{name}</c>
/// to a shared teardown log when disposed.
/// </summary>
internal sealed class RecordingForwarder : ILoggerForwarder
{
    private readonly List<string>? _teardown;
    private readonly bool _throwOnDispose;
    private int _disposeCount;

    public RecordingForwarder(string name, ILoggerFactory factory, List<string>? teardown = null, bool throwOnDispose = false)
    {
        Name = name;
        Factory = factory;
        _teardown = teardown;
        _throwOnDispose = throwOnDispose;
    }

    public string Name { get; }

    public ILoggerFactory Factory { get; }

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        _teardown?.Add("forwarder:" + Name);

        if (_throwOnDispose)
        {
            throw new InvalidOperationException("forwarder rejected dispose");
        }
    }
}

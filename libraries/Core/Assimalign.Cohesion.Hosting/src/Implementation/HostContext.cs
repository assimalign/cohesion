using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.Hosting;

public abstract class HostContext : IHostContext
{
    private readonly Lock _lock;
    private readonly HostId _hostId;
    private HostState _state;

    protected HostContext()
    {
        _lock = new Lock();
        _hostId = HostId.New();
        _state = HostState.Unknown;
    }

    public HostId HostId => _hostId;
    public HostState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }
    public abstract IHostEnvironment Environment { get; }
    public abstract IServiceProvider? ServiceProvider { get; }
    public abstract IEnumerable<IHostService> HostedServices { get; }

    internal Action? ShutdownCallback { get; set; }
    internal void SetState(HostState state)
    {
        lock (_lock)
        {
            _state = state;
        }
    }

    public void Shutdown()
    {
        InvalidOperationException.ThrowIf(ShutdownCallback is null, "Host has not started.");

        ShutdownCallback.Invoke();
    }
}

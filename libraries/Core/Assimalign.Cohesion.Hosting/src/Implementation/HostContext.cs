using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Hosting;

using Cohesion.Internal;

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
        set
        {
            lock (_lock)
            {
                _state = value;
            }
        }
    }
    public abstract FileSystemPath? ContentRootPath { get; }
    public abstract IHostEnvironment Environment { get; } 
    public virtual IServiceProvider? ServiceProvider { get;}
    public abstract IEnumerable<IHostService> HostedServices { get; }
    internal Action? ShutdownCallback { get; set; }

    public void Shutdown()
    {
        if (ShutdownCallback is null)
        {
            ThrowHelper.ThrowInvalidOperationException("Host has not started.");
        }
        ShutdownCallback.Invoke();
    }
}

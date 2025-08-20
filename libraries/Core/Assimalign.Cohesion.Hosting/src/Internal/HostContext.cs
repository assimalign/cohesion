using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.Hosting.Internal;

internal sealed class HostContext : IHostContext
{
    private Lock _lock = new Lock();
    private HostState _state;

    public HostContext()
    {
        _state = HostState.Unknown;
    }

    public HostId HostId { get; init; }
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
            lock(_lock)
            {
                _state = value;
            }
        }
    }
    public Action? ShutdownCallback { get; set; }
    public FileSystemPath? ContentRootPath { get; set; }
    public IHostEnvironment Environment { get; init; } = default!;
    public IServiceProvider? ServiceProvider { get; set; }
    public List<IHostService> HostedServices { get; init; } = new();
    IEnumerable<IHostService> IHostContext.HostedServices => HostedServices.AsReadOnly();

    public void Shutdown()
    {
        if (ShutdownCallback is null)
        {
            ThrowHelper.ThrowInvalidOperationException("Host has not started.");
        }
        ShutdownCallback.Invoke();
    }
}
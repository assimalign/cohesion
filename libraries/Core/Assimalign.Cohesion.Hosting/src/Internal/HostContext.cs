using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting.Internal;

internal sealed class HostContext : IHostContext
{
    private object stateLock = new object();
    private HostState state;


    public HostState State
    {
        get
        {
            lock (stateLock)
            {
                return state;
            }
        }
        set => state = value;
    }
    public Action? ShutdownCallback { get; set; }
    public string? ContentRootPath { get; set; }
    public IHostEnvironment Environment { get; init; } = default!;
    public IServiceProvider? ServiceProvider { get; set; }
    public List<IHostService> HostedServices { get; init; } = new();
    IEnumerable<IHostService> IHostContext.HostedServices => HostedServices;

    public void Shutdown()
    {
        if (ShutdownCallback is null)
        {
            ThrowHelper.ThrowInvalidOperationException("Host has not started.");
        }
        ShutdownCallback.Invoke();
    }
}
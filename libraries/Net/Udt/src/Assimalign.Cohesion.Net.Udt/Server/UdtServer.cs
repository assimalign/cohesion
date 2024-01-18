
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt;

using Assimalign.Cohesion.Net.Hosting;
using Assimalign.Cohesion.Net.Udt.Internal;


public sealed class UdtServer : IHostServer
{
    internal UdtServer(UdtServerOptions options)
    {
        this.State = new UdtServerState();
    }

    public IHostServerState State { get; }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }
}

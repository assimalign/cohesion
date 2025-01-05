
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Net.Udt.Internal;


public sealed class UdtServer : IHostService
{
    internal UdtServer(UdtServerOptions options)
    {
        //this.State = new UdtServerState();
    }


    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

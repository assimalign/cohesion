
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace Assimalign.Cohesion.Net.Identity;

using Assimalign.Cohesion.Net.Hosting;


public sealed class IdentityServer : IHostServer
{
    public IHostServerState State => throw new NotImplementedException();

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

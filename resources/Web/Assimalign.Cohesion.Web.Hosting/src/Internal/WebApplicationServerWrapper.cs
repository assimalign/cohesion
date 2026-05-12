using Assimalign.Cohesion.Hosting;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

internal class WebApplicationServerWrapper : IWebApplicationServer, IHostService
{
    private readonly IWebApplicationServer _server;

    internal WebApplicationServerWrapper(IWebApplicationServer server)
    {
        _server = server;
    }

    public ServiceId Id { get; } = ServiceId.New();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return _server.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _server.StopAsync(cancellationToken);
    }
}

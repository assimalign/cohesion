using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

using Assimalign.Cohesion.Hosting.Internal;

public sealed class HostBuilder : IHostBuilder
{
    private static bool isBuilt;
    private readonly IList<IHostServer> servers;
    private readonly HostOptions options;
    private HostServerStateCallbackAsync callback;

    public HostBuilder()
    {
        this.servers = new List<IHostServer>();
        this.options = new();
        this.callback = async state =>
        {
            Console.WriteLine("");
        };
    }

    /// <inheritdoc/>
    public IHostBuilder AddServerStateCallback(HostServerStateCallbackAsync callback)
    {
        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        this.callback = callback;

        return this;
    }

    /// <inheritdoc/>
    public IHostBuilder AddServer(IHostServer server)
    {
        if (server is null)
        {
            throw new ArgumentNullException(nameof(server));
        }

        this.servers.Add(server);

        return this;
    }
    
    /// <inheritdoc/>
    public IHostBuilder AddServer(IHostServerBuilder builder) => AddServer(builder.Build());

    /// <summary>
    /// Configure options the default IHost.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public IHostBuilder ConfigureOptions(Action<HostOptions> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        configure.Invoke(options);

        return this;
    }

    /// <inheritdoc/>
    public IHost Build()
    {
        if (isBuilt)
        {
            throw new InvalidHostBuildException("The host has already been built.");
        }
        if (servers.Count <= 0)
        {
            throw new InvalidHostBuildException("At least one server must be added to the host before building.");
        }

        var host = new Host(new HostContext()
        {
            Servers = servers,
            ServerStateCallback = callback,
            StateCheckInterval = options.StateCheckInterval,
            ThrowExceptionOnServerStartFailure = options.StopAllServersOnSingleFailure,
        });

        isBuilt = true;

        return host;
    }

    public static IHostBuilder Create() => new HostBuilder();
}

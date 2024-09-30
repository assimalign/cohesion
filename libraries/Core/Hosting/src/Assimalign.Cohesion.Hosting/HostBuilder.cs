using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting;

using Assimalign.Cohesion.Hosting.Internal;

public sealed class HostBuilder : IHostBuilder
{
    private bool isBuilt;

    private readonly HostOptions options;
    private readonly List<Action<HostContext>> onServiceAdd = new();
    private readonly List<Action<HostContext>> onServiceProviderAdd = new();

    public HostBuilder(HostOptions options)
    {
        if (options is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(options));
        }
        this.options = options;
    }

    /// <inheritdoc/>
    public IHostBuilder AddService(IHostService service)
    {
        if (service is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(service));
        }
        onServiceAdd.Add(context =>
        {
            context.HostedServices.Add(service);
        });
        return this;
    }

    /// <inheritdoc/>
    public IHostBuilder AddService(Func<IHostContext, IHostService> configure)
    {
        if (configure is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(configure));
        }
        onServiceAdd.Add(context =>
        {
            context.HostedServices.Add(configure.Invoke(context));
        });
        return this;
    }

    /// <inheritdoc/>
    public IHostBuilder AddServiceProvider(IServiceProvider serviceProvider)
    {
        if (serviceProvider is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(serviceProvider));
        }
        onServiceProviderAdd.Add(context =>
        {
            context.ServiceProvider = serviceProvider;
        });
        return this;
    }

    /// <inheritdoc/>
    public IHostBuilder AddServiceProvider(Func<IHostContext, IServiceProvider> method)
    {
        if (method is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(method));
        }
        onServiceProviderAdd.Add(context =>
        {
            context.ServiceProvider = method.Invoke(context);
        });
        return this;
    }

    /// <inheritdoc/>
    public IHost Build()
    {
        if (isBuilt == true)
        {
            ThrowHelper.ThrowInvalidOperationException("The host has already been built.");
        }

        var host = new Host(options);

        OnBuild(host, onServiceProviderAdd);
        OnBuild(host, onServiceAdd);

        isBuilt = true;

        return host;
    }


    private void OnBuild(Host host, IList<Action<HostContext>> actions)
    {
        foreach (var action in actions)
        {
            action.Invoke(host.Context);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    public static IHostBuilder Create() => new HostBuilder(new());
    /// <summary>
    /// 
    /// </summary>
    public static IHostBuilder Create(Action<HostOptions> configure)
    {
        if (configure is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(configure));
        }
        var options = new HostOptions();

        configure.Invoke(options);

        return new HostBuilder(options);
    }
}
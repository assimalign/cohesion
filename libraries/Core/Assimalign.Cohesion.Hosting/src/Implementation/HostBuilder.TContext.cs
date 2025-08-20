using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Hosting.Internal;

public abstract class HostBuilder<TContext> : IHostBuilder where TContext : HostContext
{
    private bool _isBuilt;

    private readonly HostOptions<TContext> _options;
    private readonly List<Action<TContext>> _onServiceAdd = new();
    private readonly List<Action<TContext>> _onServiceProviderAdd = new();

    public HostBuilder(HostOptions<TContext> options)
    {
        _options = ThrowHelper.ThrowIfNull(options);
    }

    /// <inheritdoc/>
    public HostBuilder<TContext> AddService(IHostService service)
    {
        if (service is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(service));
        }
        _onServiceAdd.Add(context =>
        {
            context.HostedServices.Add(service);
        });
        return this;
    }

    /// <inheritdoc/>
    public HostBuilder<TContext> AddService(Func<TContext, IHostService> configure)
    {
        if (configure is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(configure));
        }
        _onServiceAdd.Add(context =>
        {
            context.HostedServices.Add(configure.Invoke(context));
        });
        return this;
    }

    /// <inheritdoc/>
    public HostBuilder<TContext> AddServiceProvider(IServiceProvider serviceProvider)
    {
        if (serviceProvider is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(serviceProvider));
        }
        _onServiceProviderAdd.Add(context =>
        {
            context.ServiceProvider = serviceProvider;
        });
        return this;
    }

    /// <inheritdoc/>
    public HostBuilder<TContext> AddServiceProvider(Func<TContext, IServiceProvider> method)
    {
        if (method is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(method));
        }
        _onServiceProviderAdd.Add(context =>
        {
            context.ServiceProvider = method.Invoke(context);
        });
        return this;
    }

    


    public abstract Host<>
    
        
        /// <inheritdoc/>
    public IHost Build()
    {
        if (_isBuilt == true)
        {
            ThrowHelper.ThrowInvalidOperationException("The host has already been built.");
        }

        var host = new Host<TContext>(_options);

        OnBuild(host, _onServiceProviderAdd);
        OnBuild(host, _onServiceAdd);

        _isBuilt = true;

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
    //public static IHostBuilder Create() => new HostBuilder(new());

    ///// <summary>
    ///// 
    ///// </summary>
    //public static IHostBuilder Create(Action<HostOptions<TContext>> configure)
    //{
    //    if (configure is null)
    //    {
    //        ThrowHelper.ThrowArgumentNullException(nameof(configure));
    //    }
    //    var options = new HostOptions();

    //    configure.Invoke(options);

    //    return new HostBuilder(options);
    //}'

    IHost IHostBuilder.Build()
    {
        throw new NotImplementedException();
    }

    IHostBuilder IHostBuilder.AddService(IHostService service)
    {
        return AddService(service);
    }
    IHostBuilder IHostBuilder.AddService(Func<IHostContext, IHostService> configure)
    {
        throw new NotImplementedException();
    }

    IHostBuilder IHostBuilder.AddServiceProvider(IServiceProvider serviceProvider)
    {
        return AddServiceProvider(serviceProvider);
    }

    public IHostBuilder AddServiceProvider(Func<IHostContext, IServiceProvider> serviceProvider)
    {
        throw new NotImplementedException();
    }
}
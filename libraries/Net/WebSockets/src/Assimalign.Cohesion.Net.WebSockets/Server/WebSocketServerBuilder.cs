
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.WebSockets;

using Assimalign.Cohesion.Net.Hosting;

public sealed class WebSocketServerBuilder : IHostServerBuilder
{
    private Func<IServiceProvider> serviceProviderAction;
    private IList<Action<IServiceProvider, WebSocketServerOptions>> settings;

    public WebSocketServerBuilder()
    {
        this.settings = new List<Action<IServiceProvider, WebSocketServerOptions>>();
        this.serviceProviderAction = () => default!;
    }


    /// <summary>
    /// Overrides the default server settings.
    /// </summary>
    /// <param name="configure">A delegate action to for setting up options.</param>
    /// <returns><see cref="WebSocketServerBuilder"/></returns>
    public WebSocketServerBuilder ConfigureServer(Action<WebSocketServerOptions> configure)
    {
        return ConfigureServer((serviceProvider, options) =>
        {
            configure.Invoke(options);
        });
    }

    /// <summary>
    /// Overrides the default server settings. 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns><see cref="WebSocketServerBuilder"/></returns>
    public WebSocketServerBuilder ConfigureServer(Action<IServiceProvider, WebSocketServerOptions> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        settings.Add(configure);

        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <param name="configure"></param>
    /// <returns></returns>
    public WebSocketServerBuilder ConfigureServer<T1>(Action<T1, WebSocketServerOptions> configure)
    {
        return ConfigureServer((serviceProvider, options) =>
        {
            configure.Invoke(serviceProvider.GetService(typeof(T1)) is T1 instance ? instance : default!, options);
        });
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <param name="configure"></param>
    /// <returns></returns>
    public WebSocketServerBuilder ConfigureServer<T1, T2>(Action<T1, T2, WebSocketServerOptions> configure)
    {
        return ConfigureServer((serviceProvider, options) =>
        {
            var instance1 = default(T1)!;
            var instance2 = default(T2)!;

            if (serviceProvider.GetService(typeof(T1)) is T1 cast1)
            {
                instance1 = cast1;
            }
            if (serviceProvider.GetService(typeof(T2)) is T2 cast2)
            {
                instance2 = cast2;
            }

            configure.Invoke(instance1, instance2, options);
        });
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <param name="configure"></param>
    /// <returns></returns>
    public WebSocketServerBuilder ConfigureServer<T1, T2, T3>(Action<T1, T2, T3, WebSocketServerOptions> configure)
    {
        return ConfigureServer((serviceProvider, options) =>
        {
            var instance1 = default(T1)!;
            var instance2 = default(T2)!;
            var instance3 = default(T3)!;

            if (serviceProvider.GetService(typeof(T1)) is T1 cast1)
            {
                instance1 = cast1;
            }
            if (serviceProvider.GetService(typeof(T2)) is T2 cast2)
            {
                instance2 = cast2;
            }
            if (serviceProvider.GetService(typeof(T3)) is T3 cast3)
            {
                instance3 = cast3;
            }

            configure.Invoke(instance1, instance2, instance3, options);
        });
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <param name="configure"></param>
    /// <returns></returns>
    public WebSocketServerBuilder ConfigureServer<T1, T2, T3, T4>(Action<T1, T2, T3, T4, WebSocketServerOptions> configure)
    {
        return ConfigureServer((serviceProvider, options) =>
        {
            var instance1 = default(T1)!;
            var instance2 = default(T2)!;
            var instance3 = default(T3)!;
            var instance4 = default(T4)!;

            if (serviceProvider.GetService(typeof(T1)) is T1 cast1)
            {
                instance1 = cast1;
            }
            if (serviceProvider.GetService(typeof(T2)) is T2 cast2)
            {
                instance2 = cast2;
            }
            if (serviceProvider.GetService(typeof(T3)) is T3 cast3)
            {
                instance3 = cast3;
            }
            if (serviceProvider.GetService(typeof(T4)) is T4 cast4)
            {
                instance4 = cast4;
            }

            configure.Invoke(instance1, instance2, instance3, instance4, options);
        });
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <typeparam name="T3"></typeparam>
    /// <param name="configure"></param>
    /// <returns></returns>
    public WebSocketServerBuilder ConfigureServer<T1, T2, T3, T4, T5>(Action<T1, T2, T3, T4, T5, WebSocketServerOptions> configure)
    {
        return ConfigureServer((serviceProvider, options) =>
        {
            var instance1 = default(T1)!;
            var instance2 = default(T2)!;
            var instance3 = default(T3)!;
            var instance4 = default(T4)!;
            var instance5 = default(T5)!;

            if (serviceProvider.GetService(typeof(T1)) is T1 cast1)
            {
                instance1 = cast1;
            }
            if (serviceProvider.GetService(typeof(T2)) is T2 cast2)
            {
                instance2 = cast2;
            }
            if (serviceProvider.GetService(typeof(T3)) is T3 cast3)
            {
                instance3 = cast3;
            }
            if (serviceProvider.GetService(typeof(T4)) is T4 cast4)
            {
                instance4 = cast4;
            }
            if (serviceProvider.GetService(typeof(T5)) is T5 cast5)
            {
                instance5 = cast5;
            }

            configure.Invoke(instance1, instance2, instance3, instance4, instance5, options);

        });
    }

    /// <summary>
    /// Configures a Service Provider for the server which will passed on the <see cref="HttpContext"/>
    /// for all incoming requests.
    /// </summary>
    /// <remarks>
    /// If called more than once 
    /// </remarks>
    /// <param name="serviceProvider"></param>
    /// <returns><see cref="WebSocketServerBuilder"/></returns>
    public WebSocketServerBuilder ConfigureServiceProvider(IServiceProvider serviceProvider)
    {
        if (serviceProvider is null)
        {
            throw new ArgumentNullException(nameof(serviceProvider));
        }

        this.serviceProviderAction = () => serviceProvider;

        return this;
    }

    /// <summary>
    /// Configures a Service Provider for the server which will passed on the <see cref="HttpContext"/>
    /// for all incoming requests.
    /// </summary>
    /// <remarks>
    /// If called more than once 
    /// </remarks>
    /// <param name="configure"></param>
    /// <returns><see cref="WebSocketServerBuilder"/></returns>
    public WebSocketServerBuilder ConfigureServiceProvider(Func<IServiceProvider> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        this.serviceProviderAction = configure;

        return this;
    }


    IHostServer IHostServerBuilder.Build()
    {
        var options = new WebSocketServerOptions();
        var serviceProvider = serviceProviderAction.Invoke();

        foreach (var setting in settings)
        {
            setting.Invoke(serviceProvider, options);
        }

        return new WebSocketServer(options);
    }
}

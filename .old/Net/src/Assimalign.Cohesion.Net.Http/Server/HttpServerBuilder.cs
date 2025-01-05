using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Net.Http;

using Assimalign.Cohesion.Net.Http.Internal;

public sealed class HttpServerBuilder 
{
    private Func<IServiceProvider> serviceProviderAction;
    private IList<Action<HttpServerOptions>> onBuild;

    public HttpServerBuilder() //(IHostContext context)
    {
        this.onBuild = new List<Action<HttpServerOptions>>();
        this.serviceProviderAction = () => default!;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public HttpServerBuilder ConfigureServer(Action<HttpServerOptions> configure)
    {
        if (configure is null)
        {
            ThrowUtility.ThrowArgumentNullException(nameof(configure));
        }

        onBuild.Add(configure);

        return this;
    }

  
    public HttpServer Build()
    {
       // var options = new HttpServerOptionsInternal();
        //var serviceProvider = serviceProviderAction.Invoke();

        //foreach (var setting in settings)
        //{
        //    setting.Invoke(serviceProvider, options);
        //}

        return new HttpServer(default!);
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.WebApi;

using Hosting;
using Configuration;
using Net.Http;
using DependencyInjection;

public class WebApiApplicationBuilder
{

    internal WebApiApplicationBuilder()
    {

    }

    /// <summary>
    /// 
    /// </summary>
    public HttpServerBuilder Server { get; }
    /// <summary>
    /// 
    /// </summary>
    public ConfigurationManager Configuration { get; }

    public IServiceCollection Services { get; set; }



    public static WebApiApplicationBuilder Create()
    {
        return default;
    }
}

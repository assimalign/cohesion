using Assimalign.Cohesion.Http;
using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting;

public sealed class WebApplicationServerBuilder
{

    internal WebApplicationServerBuilder(WebApplicationBuilder builder)
    {
        
    }


    public WebApplicationServerBuilder UseServer(IWebApplicationServer server)
    {

        return this;
    }

    public WebApplicationServerBuilder UseHttps(HttpProtocol protocol)
    {


        return this;
    }
}

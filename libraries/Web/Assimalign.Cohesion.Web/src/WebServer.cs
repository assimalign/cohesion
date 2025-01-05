using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

public class WebServer
{
    private readonly WebServerOptions options;

    public WebServer(WebServerOptions options)
    {
        this.options = options;
    }
}

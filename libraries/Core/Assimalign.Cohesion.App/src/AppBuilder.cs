using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.App;

using Assimalign.Cohesion.Hosting;

public sealed class AppBuilder
{
    public AppBuilder AddService(IHostService service)
    {


        return this;
    }

    public App Build()
    {

        return default;
    }
    
}

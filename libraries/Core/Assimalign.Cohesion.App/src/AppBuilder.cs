using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.App;

using DependencyInjection;
using Configuration;

public abstract class AppBuilder<TApp> 
    where TApp : App
{
    protected AppBuilder()
    {
        Services = new ServiceCollection();
    }

    /// <summary>
    /// 
    /// </summary>
    public virtual ServiceCollection Services { get; }

    /// <summary>
    /// 
    /// </summary>
    public virtual ConfigurationManager Configuration { get; }





    public abstract TApp Build();
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Synthara;

/// <summary>
/// 
/// </summary>
public interface ISyntharaApplicationBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="resource"></param>
    /// <returns></returns>
    ISyntharaApplicationBuilder AddResource(ISyntharaResource resource);


    ISyntharaApplicationBuilder AddResource<TBuilder, >

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    ISyntharaApplication Build();
}

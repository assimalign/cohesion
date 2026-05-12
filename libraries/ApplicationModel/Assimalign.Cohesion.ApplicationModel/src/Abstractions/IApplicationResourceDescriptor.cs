using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.ApplicationModel;

public interface IApplicationResourceDescriptor
{
    /// <summary>
    /// 
    /// </summary>
    IApplicationResource Resource { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="resource"></param>
    /// <returns></returns>
    IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource);
}

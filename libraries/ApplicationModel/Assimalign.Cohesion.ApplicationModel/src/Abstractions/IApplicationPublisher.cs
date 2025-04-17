using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

public interface IApplicationPublisher
{
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    Task PublishAsync();
}

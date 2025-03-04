using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.VirtualArchitecture;

public interface IVirtualServiceGroup
{
    ServiceGroupId Id { get; }
    ServiceGroupName Name { get; }
    IEnumerable<IVirtualService> Services { get; }
}

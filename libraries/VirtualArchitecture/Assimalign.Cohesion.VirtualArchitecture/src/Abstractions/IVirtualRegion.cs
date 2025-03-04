using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.VirtualArchitecture;

public interface IVirtualRegion
{
    RegionId Id { get; }
    RegionName Name { get; }
}

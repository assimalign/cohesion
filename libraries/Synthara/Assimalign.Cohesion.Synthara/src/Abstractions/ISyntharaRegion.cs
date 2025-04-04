using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Synthara;

public interface ISyntharaRegion
{
    RegionId Id { get; }
    RegionName Name { get; }
}

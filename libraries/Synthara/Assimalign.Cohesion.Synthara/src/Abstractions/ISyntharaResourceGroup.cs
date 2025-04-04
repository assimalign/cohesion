using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Synthara;

public interface ISyntharaResourceGroup
{
    ResourceGroupId Id { get; }
    ResourceGroupName Name { get; }
    ISyntharaRegion Region { get; }
    ISyntharaAccount Account { get; }
    IEnumerable<ISyntharaResource> Resources { get; }
}

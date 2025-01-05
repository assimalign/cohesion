using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal abstract class UdtCongestionControlVirtualFactory
{
    public abstract UdtCongestionControlBase create();
    public abstract UdtCongestionControlVirtualFactory clone();
}

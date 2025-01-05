using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal struct UdtIOVector
{
    public uint[] iov_base;
    public int iov_len;
}

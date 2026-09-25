using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Internal;

internal class XorShift64
{
    UInt64 _x = 88172645463325252UL;

    public XorShift64(UInt64 seed)
    {
        if (seed != 0)
        {
            _x = seed;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public UInt64 Next()
    {
        _x = _x ^ (_x << 7);
        return _x = _x ^ (_x >> 9);
    }
}

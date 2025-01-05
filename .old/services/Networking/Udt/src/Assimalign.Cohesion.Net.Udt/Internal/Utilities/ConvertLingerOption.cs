using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal static class ConvertLingerOption
{
    public unsafe static LingerOption FromVoidPointer(void* option)
    {
        bool* pEnabled = (bool*)option;
        bool bEnabled = *pEnabled;

        int* pTime = (int*)(++pEnabled);
        int timeSeconds = *pTime;

        return new LingerOption(bEnabled, timeSeconds);
    }

    public unsafe static void ToVoidPointer(LingerOption lingerOption, void* option)
    {
        bool* pEnabled = (bool*)option;
        *pEnabled = lingerOption.Enabled;

        int* pTime = (int*)(++pEnabled);
        *pTime = lingerOption.LingerTime;
    }
}
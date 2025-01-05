using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal enum UdtEpollOptions
{
    // this values are defined same as linux epoll.h
    // so that if system values are used by mistake, they should have the same effect
    UDT_EPOLL_IN = 0x1,
    UDT_EPOLL_OUT = 0x4,
    UDT_EPOLL_ERR = 0x8
};
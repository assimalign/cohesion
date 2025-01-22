using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Dns;

public enum DnsOpCode : byte
{
    StandardQuery = 0,
    InverseQuery = 1,
    ServerStatusRequest = 2,
    Notify = 4,
    Update = 5,
    DnsStatefulOperations = 6
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

/* https://tools.ietf.org/html/rfc7540#section-6.3
    +-+-------------------------------------------------------------+
    |E|                  Stream Dependency (31)                     |
    +-+-------------+-----------------------------------------------+
    |   Weight (8)  |
    +-+-------------+
*/
internal partial class Http2Frame
{
    public int PriorityStreamDependency { get; set; }
    public bool PriorityIsExclusive { get; set; }
    public byte PriorityWeight { get; set; }
    public void PreparePriority(int streamId, int streamDependency, bool exclusive, byte weight)
    {
        PayloadLength = 5;
        Type = Http2FrameType.Priority;
        StreamId = streamId;
        PriorityStreamDependency = streamDependency;
        PriorityIsExclusive = exclusive;
        PriorityWeight = weight;
    }
}
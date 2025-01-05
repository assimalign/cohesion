using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal partial class Http3RawFrame
{
    public void PrepareGoAway()
    {
        Length = 0;
        Type = Http3FrameType.GoAway;
    }
}

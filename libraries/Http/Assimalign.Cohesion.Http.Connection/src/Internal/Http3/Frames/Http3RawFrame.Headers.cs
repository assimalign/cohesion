using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

internal partial class Http3RawFrame
{
    public void PrepareHeaders()
    {
        Length = 0;
        Type = Http3FrameType.Headers;
    }
}

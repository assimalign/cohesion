using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal partial class Http3RawFrame
{
    public long Length { get; set; }

    public Http3FrameType Type { get; internal set; }

    public string FormattedType => Http3Formatting.ToFormattedType(Type);

    public override string ToString()
    {
        return $"{FormattedType} Length: {Length}";
    }
}

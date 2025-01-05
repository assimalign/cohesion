using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal readonly struct Http2PeerSetting
{
    public Http2PeerSetting(Http2SettingsParameter parameter, uint value)
    {
        Parameter = parameter;
        Value = value;
    }

    public Http2SettingsParameter Parameter { get; }

    public uint Value { get; }
}


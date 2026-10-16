using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Identifies the version byte of a BMFF full box.</summary>
public enum BmffVersion : uint
{
    /// <summary>Full-box version zero.</summary>
    Version0 = 0,
    /// <summary>Full-box version one.</summary>
    Version1 = 1
}

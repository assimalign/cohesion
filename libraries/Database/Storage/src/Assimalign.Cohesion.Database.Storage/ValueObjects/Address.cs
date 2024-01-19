using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;


/// <summary>
/// An address is a multi-part 
/// Storage Structure:
/// 
/// 345932:
/// </summary>
public readonly struct Address
{
    public Address()
    {
        
    }



    /// <summary>
    /// Depth 0: Root Segment Stream Position
    /// -> Depth 1: Root Segment Stream Position + Offset
    /// --> Depth 2: Depth 1 + Offset
    /// </summary>
    /// <returns></returns>

    public IEnumerable<Tuple<int, long>> GetSegmentOffsets()
    {
        return default;
    }





    public static implicit operator Span<byte> (Address address)
    {

    }
}

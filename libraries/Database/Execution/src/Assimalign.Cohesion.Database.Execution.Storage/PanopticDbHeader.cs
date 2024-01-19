using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution.Storage;

using Assimalign.Cohesion.Database.Execution.Storage.ValueTypes;


public abstract class Cohesion.DatabaseHeader : ICohesion.DatabaseStorageSegmentHeader
{
    public const int SegmentIdOffset = 16;      // 16 bytes
    public const int SegmentNameOffset = 255;   // 255 bytes for max 255 character name
    public const int SegmentSizeOffset = 8;     // 8 bytes for Signed Int64


    /// <summary>
    /// 
    /// </summary>
    public StorageId SegmentId { get; set; }
    
    /// <summary>
    /// 
    /// </summary>
    public ImmutableName SegmentName { get; set; }

    /// <summary>
    /// Represents the size of the segment in bytes
    /// </summary>
    public long SegmentSize { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public int HeaderSize { get; }
}

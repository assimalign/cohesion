using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution.Storage;

using Assimalign.Cohesion.Database.Execution.Storage.ValueTypes;

public interface ICohesion.DatabaseStorageSegmentHeader
{
    /// <summary>
    /// 
    /// </summary>
    StorageId Id { get; }
    /// <summary>
    /// 
    /// </summary>
    ImmutableName Name { get; set; }
    /// <summary>
    /// 
    /// </summary>
    long Size { get; set; }
    /// <summary>
    /// 
    /// </summary>
    long FreeSpace { get; }
    /// <summary>
    /// 
    /// </summary>
    int HeaderSize { get; }
}
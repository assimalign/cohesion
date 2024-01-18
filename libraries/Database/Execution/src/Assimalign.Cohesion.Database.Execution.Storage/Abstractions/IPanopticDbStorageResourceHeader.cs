using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Execution.Storage;

using Assimalign.PanopticDb.Execution.Storage.ValueTypes;

public interface IPanopticDbStorageResourceHeader
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

    /// <summary>
    /// 
    /// </summary>
    PanopticDbStorageResourceType StorageType { get; }
}


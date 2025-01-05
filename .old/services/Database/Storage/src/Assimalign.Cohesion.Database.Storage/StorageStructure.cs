using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// The storage structure specifies how segments are organized in the data file.
/// </summary>
public enum StorageStructure
{

    Unknown = 0,
    /// <summary>
    /// 
    /// </summary>
    BPlusTree,
    /// <summary>
    /// Indexed Sequential Access Method
    /// </summary>
    Isam,
    /// <summary>
    /// 
    /// </summary>
    FlatFile
}

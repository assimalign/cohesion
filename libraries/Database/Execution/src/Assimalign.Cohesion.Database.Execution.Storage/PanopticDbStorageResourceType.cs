using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Execution.Storage;

public enum PanopticDbStorageResourceType : byte
{
    Unknown = 0,
    CacheStorage = 1,
    BlobStorage = 2,
    SqlStorage = 3,
    DocumentStorage = 4,
    LogStorage = 5
}
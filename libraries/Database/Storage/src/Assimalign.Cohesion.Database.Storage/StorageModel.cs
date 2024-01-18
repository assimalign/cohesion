using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Storage;

/// <summary>
/// <see cref="StorageModel"/> represents the database storage structure.
/// </summary>
public enum StorageModel
{
    Unknown = -1,
    Sql,
    Document,
    KeyValueStore,
    Blob,
    TableStore,
    Graph
}

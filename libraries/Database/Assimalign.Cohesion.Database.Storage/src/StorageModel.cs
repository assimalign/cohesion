using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// <see cref="StorageModel"/> represents the database storage structure.
/// </summary>
public enum StorageModel : byte
{
    Custom,
    Sql,
    Document,
    KeyValueStore,
    Blob,
    Graph
}

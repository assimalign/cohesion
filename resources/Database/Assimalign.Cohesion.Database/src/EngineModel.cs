using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Database;

public enum EngineModel : byte
{

    Custom,
    Sql,
    Document,
    KeyValueStore,
    Blob,
    Graph
}

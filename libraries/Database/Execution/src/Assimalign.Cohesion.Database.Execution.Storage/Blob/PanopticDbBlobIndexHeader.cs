using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution.Storage.Blob;

public class Cohesion.DatabaseBlobIndexHeader
{
    public int MyProperty { get; set; }

    /// <summary>
    /// The size of the index
    /// </summary>
    public ulong Size { get; set; }
}


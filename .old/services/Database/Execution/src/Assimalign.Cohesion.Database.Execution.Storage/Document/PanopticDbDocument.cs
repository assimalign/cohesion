using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution.Storage.Document;

//var d = 1.0d;  // double
//var d0 = 1.0;   // double
//var d1 = 1e+3;  // double
//var d2 = 1e-3;  // double
//var f = 1.0f;  // float
//var m = 1.0m;  // decimal
//var i = 1;     // int
//var ui = 1U;    // uint
//var ul = 1UL;   // ulong
//var l = 1L;    // long

public class Cohesion.DatabaseDocument
{
    private const int DocumentSizeMax = 5000000;        // 5 MB     - The Maximum possible size the document can be
    private const int DocumentPartitionSize = 100000;   // 0.10 MB  - The size that will split up the document


    public Cohesion.DatabaseDocumentHeader Header { get; set; }



}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Execution.Storage;

public enum Cohesion.DatabaseStorageSegmentType
{
    // Document DB Segment Types
    Document,
    DocumentPartition,
    DocumentPartitionIndex,
    DocumentCollection,
    DocumentCollectionIndex,
    DocumentCollectionStructure,

    // Blob DB Segment Types
    Blob,
    BlobDirectory,
    BlobDirectoryIndex,

    // SQL DB Segment Types
}
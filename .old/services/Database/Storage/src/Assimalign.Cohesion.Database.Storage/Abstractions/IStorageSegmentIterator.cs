using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// A Breadth-first Iterator
/// </summary>
public interface IStorageSegmentIterator : IEnumerator<IStorageSegment>
{
    /// <summary>
    /// Represents the current depth of the iterator.
    /// </summary>
    int Depth { get; }


    bool Next(out IStorageSegment segment);


    bool MoveTo(Address address);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Execution.Storage;

public interface IPanopticDbStorageSegment
{
    /// <summary>
    /// Specifies the type of segment currently being processed.
    /// </summary>
    PanopticDbStorageSegmentType SegmentType { get; set; }

    /// <summary>
    /// Gets the Segment Header of the
    /// </summary>
    IPanopticDbStorageSegmentHeader SegmentHeader { get; }

    /// <summary>
    /// Defines whether the segment is set to a fixed length
    /// or is can grow dynamically.
    /// </summary>
    bool IsFixedLength { get; }

    /// <summary>
    /// A callback function used when the segment is shifted.
    /// </summary>
    Action SegmentPointerCallback { get; }

    /// <summary>
    /// a callback function used when an insertion is made to 
    /// the segment to update the index strategy.
    /// </summary>
    Action SegmentInsertionCallback { get; }

    /// <summary>
    /// 
    /// </summary>
    Action SegmentDeletionCallback { get; }

    /// <summary>
    /// Determines whether the 
    /// </summary>
    /// <param name="size"></param>
    /// <returns></returns>
    bool CanInsert(long size);

    /// <summary>
    /// Allocates an amount of space
    /// </summary>
    /// <param name="position"></param>
    /// <param name="size"></param>
    void Allocate(long position, long size);

    /// <summary>
    /// Shifts the segment to the specified position
    /// </summary>
    /// <param name="position"></param>
    void Shift(long position);

    /// <summary>
    /// 
    /// </summary>
    Stream SegmentContent { get; }


    void Update();

    void Insert();

    void Delete();
}


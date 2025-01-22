using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

/*
 ++++++++++++++++++++++++++++++++++++++++++
 + Segment ID       16 Bytes
 + Offset
 + 



 */

/// <summary>
/// A segment represents a logical unit of storage that is used to store data.
/// Segments are stored in various forms such as ordered/unordered flat files, B+-Tree, and Hash Tables, etc.
/// 
/// It's important to note that Segments and Units are not the same. 
/// - Semgents: Give Storage resources uniform structure and allow 
/// - Units: Are the raw data stored
/// </summary>
public interface IStorageSegment
{
    /// <summary>
    /// 
    /// </summary>
    SegmentId Id { get; }
    /// <summary>
    /// 
    /// </summary>
    SegmentLock Lock { get; }
    /// <summary>
    /// Represents the segment address.
    /// </summary>
    Address Address { get; }
    /// <summary>
    /// Retreives a single <see cref="IStorageUnit"/> ursing a raw offset.
    /// </summary>
    /// <param name="offset"></param>
    /// <returns></returns>
    //IStorageUnit GetUnit(long offset);
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IStorageUnitIterator GetUnitIterator();
}
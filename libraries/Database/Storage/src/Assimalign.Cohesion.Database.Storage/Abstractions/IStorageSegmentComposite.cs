using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.PanopticDb.Storage;

/// <summary>
/// 
/// </summary>
public interface IStorageSegmentComposite : IStorageSegment
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="units"></param>
    void ShiftUp(int units);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="units"></param>
    void ShiftDown(int units);
    /// <summary>
    /// 
    /// </summary>
    IStorageSegmentIterator GetSegmentIterator();
}
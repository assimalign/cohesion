using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Synthara;

/// <summary>
/// 
/// </summary>
public interface ISyntharaResource 
{
    /// <summary>
    /// 
    /// </summary>
    ResourceId Id { get; }

    /// <summary>
    /// 
    /// </summary>
    ResourceName Name { get; }

    /// <summary>
    /// 
    /// </summary>
    ISyntharaNode Node { get; }
}

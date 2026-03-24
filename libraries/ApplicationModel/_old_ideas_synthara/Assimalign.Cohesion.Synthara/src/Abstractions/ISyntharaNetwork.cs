using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Synthara;

/// <summary>
/// 
/// </summary>
public interface ISyntharaNetwork
{
    /// <summary>
    /// 
    /// </summary>
    IEnumerable<ISyntharaNode> Nodes { get; }

    /// <summary>
    /// 
    /// </summary>
    IEnumerable<ISyntharaNetworkSubnet> Subnets { get; }
}
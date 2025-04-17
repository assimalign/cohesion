using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Synthara;

/// <summary>
/// 
/// </summary>
public interface ISyntharaAccount
{
    /// <summary>
    /// 
    /// </summary>
    AccountId Id { get; }

    /// <summary>
    /// The name 
    /// </summary>
    AccountName Name { get; }

    /// <summary>
    /// 
    /// </summary>
    IEnumerable<ISyntharaNode> Nodes { get; }

    /// <summary>
    /// 
    /// </summary>
    IEnumerable<ISyntharaResource> Resources { get; }
}